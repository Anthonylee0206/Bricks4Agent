using Broker.Helpers;
using BrokerCore.Contracts;
using BrokerCore.Models;
using BrokerCore.Services;
using FunctionPool.Registry;
using System.Diagnostics;
using System.Text.Json;

namespace Broker.Endpoints;

/// <summary>
/// 健康檢查 API — 所有 Worker 的連線狀態、延遲、能力清單。
/// GET /api/v1/health/workers
/// </summary>
public static class HealthCheckEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var hc = group.MapGroup("/health");

        hc.MapGet("/workers", async (
            IWorkerRegistry registry,
            IExecutionDispatcher dispatcher,
            CancellationToken ct) =>
        {
            var capabilities = new[]
            {
                ("quote-worker",    "quote.prices",     "get_prices"),
                ("strategy-worker", "strategy.signal",  "list"),
                ("risk-worker",     "risk.check",       "get_rules"),
                ("trading-worker",  "trading.account",  "list_exchanges"),
            };

            var workers = new List<object>();
            foreach (var (name, cap, route) in capabilities)
            {
                var available = registry.HasAvailableWorker(cap);
                long latencyMs = -1;
                string status = "disconnected";
                string? error = null;

                if (available)
                {
                    var sw = Stopwatch.StartNew();
                    try
                    {
                        var result = await dispatcher.DispatchAsync(new ApprovedRequest
                        {
                            RequestId = Guid.NewGuid().ToString("N"),
                            CapabilityId = cap, Route = route, Payload = "{}",
                            Scope = "{}", PrincipalId = "system", TaskId = "health", SessionId = "health"
                        });
                        sw.Stop();
                        latencyMs = sw.ElapsedMilliseconds;
                        status = result.Success ? "healthy" : "error";
                        if (!result.Success) error = result.ErrorMessage;
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        latencyMs = sw.ElapsedMilliseconds;
                        status = "error";
                        error = ex.Message;
                    }
                }

                workers.Add(new
                {
                    worker = name,
                    capability = cap,
                    status,
                    connected = available,
                    latency_ms = latencyMs,
                    error,
                });
            }

            var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();

            return Results.Ok(ApiResponseHelper.Success(new
            {
                broker_status = "running",
                uptime_seconds = (int)uptime.TotalSeconds,
                uptime_display = $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s",
                worker_count = workers.Count(w => ((dynamic)w).connected),
                workers,
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

        // GET /api/v1/health/alerts?since_minutes=360&min_severity=2 — 平台健康/worker 觀測告警
        // 補 a4377fab 的閉環:HealthScoreSnapshotService 記的 HEALTH_SCORE_CRITICAL 之前無 HTTP
        // 出口、外部 watchdog 無法輪詢。這裡只走既有 ObservationService.GetAlerts、純讀、不外連。
        // dead-man(讀取時計算):寫入 loop 若死掉無法自我回報,由本端點在每次讀取時檢查
        // 「最新 snapshot 距今多久」——超過 3×tick 沒新資料 = 管線停擺,直接在回應裡置頂一條
        // 合成告警(computed=true、非 DB 事件)。外部 watchdog 只要輪詢本端點就同時覆蓋
        // 「worker 不健康」與「觀測管線本身死掉」兩種失效。
        hc.MapGet("/alerts", (
            HttpContext ctx, IObservationService observations,
            Broker.Services.HealthScoreSnapshotService snapshots) =>
        {
            var sinceMin = int.TryParse(ctx.Request.Query["since_minutes"].ToString(), out var s)
                ? Math.Clamp(s, 5, 10080) : 360;  // default 6h, max 7d
            var minSev = int.TryParse(ctx.Request.Query["min_severity"].ToString(), out var ms)
                ? Math.Clamp(ms, 0, 3) : (int)ObservationSeverity.Alert;
            var since = DateTime.UtcNow.AddMinutes(-sinceMin);
            var alerts = observations.GetAlerts(since, (ObservationSeverity)minSev, limit: 200)
                .Where(e => IsPlatformHealthAlert(e.EventType))
                .ToList();

            var lastSnap = snapshots.GetLatestSnapshotTime();
            var (stalled, ageSeconds) = Broker.Services.HealthScoreSnapshotService.ComputeSnapshotStaleness(
                lastSnap, DateTime.UtcNow, Broker.Services.HealthScoreSnapshotService.SnapshotTickInterval);

            var items = new List<object>();
            if (stalled)
            {
                // 合成告警(computed on read、非 DB 事件):寫入 loop 死掉時只有讀取端算得出來
                items.Add(new
                {
                    observation_id = (string?)null,
                    event_type     = "HEALTH_SNAPSHOT_PIPELINE_STALLED",
                    severity       = ObservationSeverity.Critical.ToString(),
                    observed_at    = DateTime.UtcNow,
                    worker_id      = (string?)null,
                    trace_id       = (string?)null,
                    observed_state = $"{{\"age_seconds\":{Math.Round(ageSeconds ?? 0)},\"last_captured_at\":\"{lastSnap:O}\"}}",
                    details        = "(computed on read — dead-man check; not a stored observation event)",
                });
            }
            items.AddRange(alerts.Select(e => (object)new
            {
                observation_id = e.ObservationId,
                event_type     = e.EventType,
                severity       = e.Severity.ToString(),
                observed_at    = e.ObservedAt,
                worker_id      = e.WorkerId,
                trace_id       = e.TraceId,
                observed_state = e.ObservedState,
                details        = e.Details,
            }));

            return Results.Ok(ApiResponseHelper.Success(new
            {
                since_minutes = sinceMin,
                min_severity  = ((ObservationSeverity)minSev).ToString(),
                snapshot_pipeline = new
                {
                    stalled,
                    age_seconds      = ageSeconds.HasValue ? Math.Round(ageSeconds.Value) : (double?)null,
                    last_captured_at = lastSnap,
                    tick_seconds     = Broker.Services.HealthScoreSnapshotService.SnapshotTickInterval.TotalSeconds,
                },
                count  = items.Count,
                alerts = items,
            }));
        });
    }

    /// <summary>判定一條觀測事件是否屬「平台健康 / worker」類告警(供 /health/alerts 過濾)。</summary>
    public static bool IsPlatformHealthAlert(string eventType)
        => !string.IsNullOrEmpty(eventType)
           && (eventType.StartsWith("HEALTH_", StringComparison.Ordinal)
               || eventType.StartsWith("WORKER_", StringComparison.Ordinal));
}
