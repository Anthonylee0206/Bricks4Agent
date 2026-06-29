using BrokerCore.Data;
using BrokerCore.Services;
using FunctionPool.Container;
using FunctionPool.Models;
using FunctionPool.Registry;

namespace Broker.Services;

/// <summary>
/// Worker 健康綜合分數（0-100）。
///
/// 把分散在 IWorkerRegistry / IAuditService / IContainerManager 三個源頭的資料
/// 壓成一個能直接寫進報告 / dashboard 的單一指標。三個權重分量：
///   - Heartbeat freshness 30%（距上次心跳幾秒）
///   - Dispatch success rate 40%（過去 15min audit_events 的 SUCCEEDED 比例）
///   - Resource pressure 30%（CPU% + mem% 平均）
///
/// 狀態切點：healthy ≥80 / degraded 50-79 / critical &lt;50。
/// 沒有對應資料的分量不會懲罰、會被踢出加權平均（避免「沒接 ContainerManager 就被算 0」）。
/// </summary>
public class HealthScoreService
{
    private readonly IWorkerRegistry _registry;
    private readonly BrokerDb _db;
    private readonly IContainerManager? _containerMgr;

    public HealthScoreService(
        IWorkerRegistry registry,
        BrokerDb db,
        IContainerManager? containerMgr = null)
    {
        _registry = registry;
        _db = db;
        _containerMgr = containerMgr;
    }

    public async Task<HealthScoreReport> ComputeAsync(CancellationToken ct = default)
    {
        var workers = _registry.GetAllWorkers();
        var now = DateTime.UtcNow;

        // 取一次 container stats（一次 docker stats 呼叫、避免 per-worker 重複查）
        // 同時取 ManagedContainer 清單，建立 worker_id → container_id 的權威對應，
        // 供 ScoreResource 精確 join（取代舊版脆弱的 container_name 前綴比對）。
        IReadOnlyList<ContainerStats>? stats = null;
        var workerToContainerId = new Dictionary<string, string>(StringComparer.Ordinal);
        if (_containerMgr != null)
        {
            try { stats = await _containerMgr.GetStatsAsync(ct); }
            catch { /* container runtime 不在 → resource 分量會被略過 */ }
            try
            {
                foreach (var m in await _containerMgr.ListManagedAsync(ct))
                    if (!string.IsNullOrEmpty(m.WorkerId) && !string.IsNullOrEmpty(m.ContainerId))
                        workerToContainerId[m.WorkerId] = m.ContainerId;
            }
            catch { /* 對應拿不到 → ScoreResource 退保守後備比對 */ }
        }

        // 過去 15 min 每個 capability 的 dispatch 成功/失敗計數
        // 用 audit_events 反查（DISPATCH_SUCCEEDED / DISPATCH_FAILED 的 details JSON 裡有 worker_id）
        var since = now.AddMinutes(-15);
        var dispatchByWorker = QueryDispatchStatsByWorker(since);

        var workerScores = new List<WorkerHealthScore>();
        foreach (var w in workers)
        {
            var hb = ScoreHeartbeat(now - w.LastHeartbeat);
            var dispatch = ScoreDispatch(dispatchByWorker.TryGetValue(w.WorkerId, out var ds) ? ds : default);
            var resource = ScoreResource(stats, w.WorkerId, workerToContainerId);

            // 加權平均，跳過 null（沒資料的分量）
            var weighted = WeightedAvg(
                (hb.Score, 0.30),
                (dispatch?.Score, 0.40),
                (resource?.Score, 0.30));

            workerScores.Add(new WorkerHealthScore
            {
                WorkerId      = w.WorkerId,
                Capabilities  = w.Capabilities,
                State         = w.State.ToString(),
                Score         = weighted,
                Status        = StatusFor(weighted),
                Heartbeat     = hb,
                Dispatch      = dispatch,
                Resource      = resource,
            });
        }

        var overall = workerScores.Count > 0
            ? (int)Math.Round(workerScores.Average(w => (double)w.Score))
            : 100;

        return new HealthScoreReport
        {
            GeneratedAt    = now,
            OverallScore   = overall,
            OverallStatus  = StatusFor(overall),
            WorkerCount    = workerScores.Count,
            HealthyCount   = workerScores.Count(w => w.Status == "healthy"),
            DegradedCount  = workerScores.Count(w => w.Status == "degraded"),
            CriticalCount  = workerScores.Count(w => w.Status == "critical"),
            Workers        = workerScores,
        };
    }

    // ── 分量計算 ─────────────────────────────────────────────────────────

    public static HeartbeatScore ScoreHeartbeat(TimeSpan elapsed)
    {
        var sec = elapsed.TotalSeconds;
        int score = sec < 30 ? 100 : sec < 60 ? 50 : 0;
        var label = sec < 30 ? "fresh" : sec < 60 ? "stale" : "lost";
        return new HeartbeatScore { Score = score, Label = label, AgeSeconds = (int)sec };
    }

    public static DispatchScore? ScoreDispatch((int succeeded, int failed) ds)
    {
        var total = ds.succeeded + ds.failed;
        if (total == 0) return null;  // 沒資料 → 不計入加權
        var rate = (decimal)ds.succeeded / total * 100m;
        int score;
        string label;
        if (rate >= 99m)      { score = 100; label = "excellent"; }
        else if (rate >= 95m) { score = 70;  label = "marginal";  }
        else if (rate >= 80m) { score = 40;  label = "degraded";  }
        else                  { score = 10;  label = "failing";   }
        return new DispatchScore
        {
            Score = score, Label = label,
            Succeeded = ds.succeeded, Failed = ds.failed,
            SuccessRatePct = Math.Round(rate, 1),
        };
    }

    private static ResourceScore? ScoreResource(
        IReadOnlyList<ContainerStats>? stats,
        string workerId,
        IReadOnlyDictionary<string, string> workerToContainerId)
    {
        if (stats == null) return null;
        var s = ResolveContainerStats(stats, workerId, workerToContainerId);
        if (s == null) return null;
        var avg = (s.CpuPercent + s.MemoryPercent) / 2.0;
        int score; string label;
        if (avg < 30)      { score = 100; label = "low";      }
        else if (avg < 60) { score = 70;  label = "moderate"; }
        else if (avg < 80) { score = 40;  label = "high";     }
        else               { score = 10;  label = "critical"; }
        return new ResourceScore
        {
            Score = score, Label = label,
            CpuPct = (decimal)Math.Round(s.CpuPercent, 1),
            MemPct = (decimal)Math.Round(s.MemoryPercent, 1),
        };
    }

    /// <summary>
    /// 把一個 worker 對應到它的 ContainerStats。
    /// (1) 優先用 ManagedContainer 維護的 worker_id → container_id 權威對應，以 ContainerId 精確 join——
    ///     修掉舊版 container_name 前綴 Contains 的兩個誤配：①多個同前綴 worker 搶同一容器、
    ///     把別人的 CPU/mem 算到自己頭上；②底線式 worker_id「wkr_xxx」用 Split('-') 切不開、
    ///     整串拿去 Contains 必然失配 → resource 分量永遠被略過。
    /// (2) 無權威對應時（NoOpContainerManager / 容器未被追蹤）退保守比對：只接受 container_name
    ///     完全相等或完整 worker_id 子字串，絕不用單段前綴亂配。
    /// public static 純函數：可確定性單元測試，無需 mock docker / registry。
    /// </summary>
    public static ContainerStats? ResolveContainerStats(
        IReadOnlyList<ContainerStats> stats,
        string workerId,
        IReadOnlyDictionary<string, string> workerToContainerId)
    {
        if (stats == null || stats.Count == 0) return null;

        if (workerToContainerId != null
            && workerToContainerId.TryGetValue(workerId, out var cid)
            && !string.IsNullOrEmpty(cid))
        {
            var byId = stats.FirstOrDefault(x => ContainerIdMatch(x.ContainerId, cid));
            if (byId != null) return byId;
        }

        return stats.FirstOrDefault(x =>
                   string.Equals(x.ContainerName, workerId, StringComparison.OrdinalIgnoreCase))
               ?? stats.FirstOrDefault(x =>
                   !string.IsNullOrEmpty(x.ContainerName)
                   && x.ContainerName.Contains(workerId, StringComparison.OrdinalIgnoreCase));
    }

    // docker stats 回短 id（12 字元），ManagedContainer 可能存完整 64 字元 id；
    // 任一方是另一方的前綴即視為同一容器。
    private static bool ContainerIdMatch(string statsId, string managedId)
    {
        if (string.IsNullOrEmpty(statsId) || string.IsNullOrEmpty(managedId)) return false;
        return managedId.StartsWith(statsId, StringComparison.OrdinalIgnoreCase)
            || statsId.StartsWith(managedId, StringComparison.OrdinalIgnoreCase);
    }

    public static int WeightedAvg(params (int? value, double weight)[] entries)
    {
        double total = 0, weights = 0;
        foreach (var (v, w) in entries)
        {
            if (!v.HasValue) continue;
            total += v.Value * w;
            weights += w;
        }
        return weights == 0 ? 100 : (int)Math.Round(total / weights);
    }

    public static string StatusFor(int score)
        => score >= 80 ? "healthy" : score >= 50 ? "degraded" : "critical";

    // ── audit_events 反查每 worker 的成功/失敗計數 ─────────────────────

    private Dictionary<string, (int succeeded, int failed)> QueryDispatchStatsByWorker(DateTime since)
    {
        // details JSON 裡有 worker_id；SQLite 的 JSON1 extension 可以 json_extract
        // 預期 SQLite 已有 JSON1 builtin（since 3.38）—broker.db 升 net8 sqlite 必含。
        var rows = _db.Query<DispatchAggRow>(@"
SELECT
    json_extract(details, '$.worker_id') AS WorkerId,
    SUM(CASE WHEN event_type LIKE '%_SUCCEEDED' THEN 1 ELSE 0 END) AS Succeeded,
    SUM(CASE WHEN event_type LIKE '%_FAILED' OR event_type LIKE '%_DENIED' THEN 1 ELSE 0 END) AS Failed
FROM audit_events
WHERE occurred_at > @since
  AND (event_type LIKE 'DISPATCH_%' OR event_type LIKE 'EXECUTION_%')
  AND json_extract(details, '$.worker_id') IS NOT NULL
GROUP BY WorkerId",
            new { since });

        return rows
            .Where(r => !string.IsNullOrEmpty(r.WorkerId))
            .ToDictionary(r => r.WorkerId!, r => (r.Succeeded, r.Failed));
    }

    private class DispatchAggRow
    {
        public string? WorkerId { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────

public class HealthScoreReport
{
    public DateTime GeneratedAt { get; set; }
    public int OverallScore { get; set; }
    public string OverallStatus { get; set; } = "";
    public int WorkerCount { get; set; }
    public int HealthyCount { get; set; }
    public int DegradedCount { get; set; }
    public int CriticalCount { get; set; }
    public List<WorkerHealthScore> Workers { get; set; } = new();
}

public class WorkerHealthScore
{
    public string WorkerId { get; set; } = "";
    public List<string> Capabilities { get; set; } = new();
    public string State { get; set; } = "";
    public int Score { get; set; }
    public string Status { get; set; } = "";
    public HeartbeatScore? Heartbeat { get; set; }
    public DispatchScore? Dispatch { get; set; }
    public ResourceScore? Resource { get; set; }
}

public class HeartbeatScore
{
    public int Score { get; set; }
    public string Label { get; set; } = "";
    public int AgeSeconds { get; set; }
}

public class DispatchScore
{
    public int Score { get; set; }
    public string Label { get; set; } = "";
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public decimal SuccessRatePct { get; set; }
}

public class ResourceScore
{
    public int Score { get; set; }
    public string Label { get; set; } = "";
    public decimal CpuPct { get; set; }
    public decimal MemPct { get; set; }
}
