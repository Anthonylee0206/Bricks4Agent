using Broker.Helpers;
using FunctionPool.Models;
using FunctionPool.Registry;
using System.Diagnostics;

namespace Broker.Endpoints;

/// <summary>
/// 健康檢查 API — 所有 Worker 的連線狀態、延遲、能力清單。
/// GET /api/v1/health/workers
/// </summary>
public static class HealthCheckEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        // worker 健康與健康分數屬管理資訊，只限管理員（公開的 GET/POST /api/v1/health 另行註冊）。
        var hc = group.MapGroup("/health").RequireBrokerAdmin();

        // GET /api/v1/health/workers — 目前註冊在 pool 的每個 worker 的狀態（讀 registry，不派發）
        //
        // 2026-10 改寫。原本這裡對四個寫死的 capability 逐一派發一次真實請求當探測，有三個問題：
        //   1. 那四個 capability 在這個 repo 沒有任何 worker 註冊，所以永遠回 disconnected；
        //   2. 探測直接組 ApprovedRequest 交給 dispatcher，繞過了 PEP 的 session / grant / policy 檢查；
        //   3. 四個探測依序 await、沒有預算，一個卡住的 worker 會拖住整個請求。
        // 改成回報 registry 已經掌握的狀態（心跳、併發、State）：不產生副作用、不繞過治理、不會被卡住。
        hc.MapGet("/workers", (IWorkerRegistry registry, PoolConfig poolConfig) =>
        {
            var now = DateTime.UtcNow;
            var workers = registry.GetAllWorkers()
                .OrderBy(w => w.WorkerId, StringComparer.Ordinal)
                .Select(w => DescribeWorker(w, now, poolConfig.HeartbeatTimeout))
                .ToList();

            var uptime = now - Process.GetCurrentProcess().StartTime.ToUniversalTime();

            return Results.Ok(ApiResponseHelper.Success(new
            {
                broker_status = "running",
                uptime_seconds = (int)uptime.TotalSeconds,
                uptime_display = $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s",
                worker_count = workers.Count,
                available_count = workers.Count(w => w.Available),
                workers = workers.Select(w => new
                {
                    worker_id = w.WorkerId,
                    capabilities = w.Capabilities,
                    state = w.State,
                    status = w.Status,
                    available = w.Available,
                    heartbeat_age_seconds = w.HeartbeatAgeSeconds,
                    active_tasks = w.ActiveTasks,
                    max_concurrent = w.MaxConcurrent,
                }),
            }));
        });

        // GET /api/v1/health/score/history?since_minutes=360 — Health Score 時序
        hc.MapGet("/score/history", (
            HttpContext ctx, Broker.Services.HealthScoreSnapshotService snapSvc) =>
        {
            var sinceMin = int.TryParse(ctx.Request.Query["since_minutes"].ToString(), out var s)
                ? Math.Clamp(s, 5, 10080) : 360;  // default 6h, max 7d
            var history = snapSvc.GetHistory(sinceMin);
            return Results.Ok(ApiResponseHelper.Success(new
            {
                since_minutes = sinceMin,
                count         = history.Count,
                snapshots = history.Select(h => new
                {
                    captured_at    = h.CapturedAt,
                    overall_score  = h.OverallScore,
                    status         = h.OverallStatus,
                    worker_count   = h.WorkerCount,
                    healthy        = h.HealthyCount,
                    degraded       = h.DegradedCount,
                    critical       = h.CriticalCount,
                }),
            }));
        });

        // GET /api/v1/health/score — Worker 健康綜合分數（0-100、含三個子分量）
        hc.MapGet("/score", async (
            Broker.Services.HealthScoreService svc, CancellationToken ct) =>
        {
            var report = await svc.ComputeAsync(ct);
            return Results.Ok(ApiResponseHelper.Success(new
            {
                generated_at   = report.GeneratedAt,
                overall_score  = report.OverallScore,
                overall_status = report.OverallStatus,
                worker_count   = report.WorkerCount,
                healthy_count  = report.HealthyCount,
                degraded_count = report.DegradedCount,
                critical_count = report.CriticalCount,
                workers = report.Workers.Select(w => new
                {
                    worker_id    = w.WorkerId,
                    capabilities = w.Capabilities,
                    state        = w.State,
                    score        = w.Score,
                    status       = w.Status,
                    heartbeat = w.Heartbeat == null ? null : new
                    {
                        score = w.Heartbeat.Score,
                        label = w.Heartbeat.Label,
                        age_seconds = w.Heartbeat.AgeSeconds,
                    },
                    dispatch = w.Dispatch == null ? null : (object)new
                    {
                        score = w.Dispatch.Score,
                        label = w.Dispatch.Label,
                        succeeded = w.Dispatch.Succeeded,
                        failed = w.Dispatch.Failed,
                        success_rate_pct = w.Dispatch.SuccessRatePct,
                    },
                    resource = w.Resource == null ? null : (object)new
                    {
                        score = w.Resource.Score,
                        label = w.Resource.Label,
                        cpu_pct = w.Resource.CpuPct,
                        mem_pct = w.Resource.MemPct,
                    },
                }),
            }));
        });
    }

    public sealed record WorkerStatusView(
        string WorkerId,
        IReadOnlyList<string> Capabilities,
        string State,
        string Status,
        bool Available,
        int HeartbeatAgeSeconds,
        int ActiveTasks,
        int MaxConcurrent);

    /// <summary>
    /// status 依序判斷（先中者勝）：
    ///   lost  — 心跳已達逾時（WorkerHealthMonitor 下一輪就會移除它）
    ///   stale — 心跳超過逾時的一半
    ///   非 Ready 的 State 小寫（connecting / busy / draining / disconnected）
    ///   busy  — Ready 但併發已滿
    ///   ready — 可接收派發
    /// 門檻與 HealthScoreService.ScoreHeartbeat 一致，兩個端點對同一個 worker 不會給出矛盾的結論。
    /// </summary>
    public static WorkerStatusView DescribeWorker(WorkerInfo w, DateTime nowUtc, TimeSpan heartbeatTimeout)
    {
        var age = nowUtc - w.LastHeartbeat;
        var status =
            age >= heartbeatTimeout ? "lost"
            : age.TotalSeconds >= heartbeatTimeout.TotalSeconds / 2 ? "stale"
            : w.State != WorkerState.Ready ? w.State.ToString().ToLowerInvariant()
            : w.ActiveTasks >= w.MaxConcurrent ? "busy"
            : "ready";

        return new WorkerStatusView(
            w.WorkerId,
            w.Capabilities.ToList(),
            w.State.ToString(),
            status,
            Available: status == "ready",
            HeartbeatAgeSeconds: (int)Math.Max(0, age.TotalSeconds),
            ActiveTasks: w.ActiveTasks,
            MaxConcurrent: w.MaxConcurrent);
    }
}
