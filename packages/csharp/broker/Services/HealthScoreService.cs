using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;
using FunctionPool.Container;
using FunctionPool.Models;
using FunctionPool.Registry;

namespace Broker.Services;

/// <summary>
/// Worker 健康綜合分數（0-100）。
///
/// 三個權重分量，任一分量沒資料就不計入（剩下的重新加權）：
///   - Heartbeat freshness 30%：距上次心跳幾秒，門檻依 FunctionPool:HeartbeatTimeoutSeconds 換算
///   - Dispatch success rate 40%：過去 15 min execution_requests 中、該 worker 所提供能力的
///     Succeeded / (Succeeded + Failed)。以「能力」歸屬而非逐一 worker（見 DispatchScore.Attribution）
///   - Resource pressure 30%：CPU% + mem% 平均（需 ContainerManager 啟用）
///
/// 狀態切點：healthy ≥80 / degraded 50-79 / critical &lt;50；沒有任何 worker 時為 no_workers。
///
/// 2026-10 修正：原本的派發分量讀 audit_events 裡帶 worker_id 的 DISPATCH_* 事件，
/// 但這個 repo 沒有任何程式會寫那種事件，所以這個分量永遠沒資料、分數實際上只看心跳。
/// 改讀 BrokerService 一定會寫的 execution_requests。
/// </summary>
public class HealthScoreService
{
    private readonly IWorkerRegistry _registry;
    private readonly BrokerDb _db;
    private readonly IContainerManager? _containerMgr;
    private readonly TimeSpan _heartbeatTimeout;

    /// <summary>沒有任何 worker 時的整體狀態；分數給 0，因為這不是「健康」。</summary>
    public const string NoWorkersStatus = "no_workers";

    public HealthScoreService(
        IWorkerRegistry registry,
        BrokerDb db,
        IContainerManager? containerMgr = null,
        PoolConfig? poolConfig = null)
    {
        _registry = registry;
        _db = db;
        _containerMgr = containerMgr;
        // 心跳門檻跟著 WorkerHealthMonitor 用的同一個逾時走，否則會出現「lost 標籤永遠到不了」：
        // monitor 在逾時當下就把 worker 移出 registry，分數這邊若用更長的門檻就永遠看不到它。
        _heartbeatTimeout = poolConfig?.HeartbeatTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<HealthScoreReport> ComputeAsync(CancellationToken ct = default)
    {
        var workers = _registry.GetAllWorkers();
        var now = DateTime.UtcNow;

        // 取一次 container stats（一次 docker stats 呼叫、避免 per-worker 重複查）
        IReadOnlyList<ContainerStats>? stats = null;
        IReadOnlyList<ManagedContainer>? managed = null;
        if (_containerMgr != null)
        {
            try { stats = await _containerMgr.GetStatsAsync(ct); }
            catch { /* container runtime 不在 → resource 分量會被略過 */ }
            try { managed = await _containerMgr.ListManagedAsync(ct); }
            catch { /* 拿不到受管清單 → 退回以容器名稱比對 */ }
        }

        // 過去 15 min 每個 capability 的執行成功/失敗計數（execution_requests 是 PEP 一定會寫的表）
        var since = now.AddMinutes(-15);
        var statsByCapability = QueryExecutionStatsByCapability(since);

        var workerScores = new List<WorkerHealthScore>();
        foreach (var w in workers)
        {
            var hb = ScoreHeartbeat(now - w.LastHeartbeat, _heartbeatTimeout);
            var dispatch = ScoreDispatch(SumForCapabilities(statsByCapability, w.Capabilities));
            var resource = ScoreResource(FindStatsForWorker(stats, managed, w.WorkerId));

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

        // 沒有任何 worker 連上不是「健康」：原本這裡回 100 / healthy，等於最糟的狀態顯示成最好的分數。
        var overall = workerScores.Count > 0
            ? (int)Math.Round(workerScores.Average(w => (double)w.Score))
            : 0;

        return new HealthScoreReport
        {
            GeneratedAt    = now,
            OverallScore   = overall,
            OverallStatus  = workerScores.Count > 0 ? StatusFor(overall) : NoWorkersStatus,
            WorkerCount    = workerScores.Count,
            HealthyCount   = workerScores.Count(w => w.Status == "healthy"),
            DegradedCount  = workerScores.Count(w => w.Status == "degraded"),
            CriticalCount  = workerScores.Count(w => w.Status == "critical"),
            Workers        = workerScores,
        };
    }

    // ── 分量計算 ─────────────────────────────────────────────────────────

    /// <summary>
    /// fresh：未滿逾時的一半（預設心跳 5 秒一次，相當於漏了 3 次以內）
    /// stale：逾時的一半到逾時之間
    /// lost ：已達逾時（WorkerHealthMonitor 下一輪掃描就會把它移出 registry）
    /// </summary>
    public static HeartbeatScore ScoreHeartbeat(TimeSpan elapsed, TimeSpan timeout)
    {
        var sec = elapsed.TotalSeconds;
        var half = timeout.TotalSeconds / 2;
        var full = timeout.TotalSeconds;
        int score = sec < half ? 100 : sec < full ? 50 : 0;
        var label = sec < half ? "fresh" : sec < full ? "stale" : "lost";
        return new HeartbeatScore { Score = score, Label = label, AgeSeconds = (int)sec };
    }

    /// <summary>
    /// 把 worker 提供的每個能力的計數加總。同一能力若有多個 worker、或由 broker 行程內執行
    /// （FallbackDispatcher），計數無法再細分到個別 worker——這是 execution_requests 沒有
    /// worker 欄位的限制，結果會標 Attribution="capability" 讓讀的人知道。
    /// </summary>
    public static (int succeeded, int failed) SumForCapabilities(
        IReadOnlyDictionary<string, (int succeeded, int failed)> statsByCapability,
        IEnumerable<string> capabilities)
    {
        int ok = 0, bad = 0;
        foreach (var cap in capabilities.Distinct(StringComparer.Ordinal))
        {
            if (!statsByCapability.TryGetValue(cap, out var c)) continue;
            ok += c.succeeded;
            bad += c.failed;
        }
        return (ok, bad);
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
            Attribution = "capability",
        };
    }

    /// <summary>
    /// 找出某個 worker 對應的容器資源統計。
    ///
    /// 2026-10 改寫。原本用「容器名稱包含 workerId 第一個 '-' 之前那段」寬鬆比對,但 ContainerManager
    /// 的容器名稱是 <c>b4a-{type}-{workerId 前 12 字}</c>:workerId 一長,那段就超過 12 字而永遠比對不到,
    /// 反之若很短又會誤配到別的容器。現在依序:
    ///   1. ContainerManager 受管清單裡 WorkerId 相同的那筆 → 以容器 ID 對到統計(docker stats 給短 ID、
    ///      受管清單存完整 ID,所以用前綴相符比對)
    ///   2. 退回比對容器名稱是否以 <c>-{workerId 前 12 字}</c> 結尾(與 ContainerManager 的命名規則一致)
    /// 都對不到就回 null:這個分量不計,而不是拿別人的容器數字充數。
    /// </summary>
    public static ContainerStats? FindStatsForWorker(
        IReadOnlyList<ContainerStats>? stats,
        IReadOnlyList<ManagedContainer>? managed,
        string workerId)
    {
        if (stats == null || stats.Count == 0 || string.IsNullOrEmpty(workerId)) return null;

        var containerId = managed?.FirstOrDefault(m =>
            string.Equals(m.WorkerId, workerId, StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(m.ContainerId))?.ContainerId;
        if (!string.IsNullOrEmpty(containerId))
        {
            var byId = stats.FirstOrDefault(s => SameContainerId(s.ContainerId, containerId));
            if (byId != null) return byId;
        }

        var suffix = "-" + workerId[..Math.Min(12, workerId.Length)];
        return stats.FirstOrDefault(s =>
            (s.ContainerName ?? "").TrimStart('/').EndsWith(suffix, StringComparison.Ordinal));
    }

    /// <summary>短 ID(12 字)與完整 ID(64 字)視為同一個容器;太短的字串不算數,避免誤配。</summary>
    private static bool SameContainerId(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        if (Math.Min(a.Length, b.Length) < 12) return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static ResourceScore? ScoreResource(ContainerStats? s)
    {
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

    private static int WeightedAvg(params (int? value, double weight)[] entries)
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

    private static string StatusFor(int score)
        => score >= 80 ? "healthy" : score >= 50 ? "degraded" : "critical";

    // ── execution_requests 反查每個能力的成功/失敗計數 ─────────────────
    // 只算 Succeeded / Failed：Denied 是政策拒絕，不是 worker 出問題，不該拉低 worker 的分數。

    private Dictionary<string, (int succeeded, int failed)> QueryExecutionStatsByCapability(DateTime since)
    {
        var rows = _db.Query<ExecutionAggRow>(@"
SELECT
    capability_id AS CapabilityId,
    SUM(CASE WHEN execution_state = @succeeded THEN 1 ELSE 0 END) AS Succeeded,
    SUM(CASE WHEN execution_state = @failed    THEN 1 ELSE 0 END) AS Failed
FROM execution_requests
WHERE updated_at > @since
  AND execution_state IN (@succeeded, @failed)
GROUP BY capability_id",
            new
            {
                since,
                succeeded = (int)ExecutionState.Succeeded,
                failed = (int)ExecutionState.Failed,
            });

        return rows
            .Where(r => !string.IsNullOrEmpty(r.CapabilityId))
            .ToDictionary(r => r.CapabilityId!, r => (r.Succeeded, r.Failed), StringComparer.Ordinal);
    }

    private class ExecutionAggRow
    {
        public string? CapabilityId { get; set; }
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
    /// <summary>"capability"：計數依能力歸屬，無法區分同能力的多個 worker 或行程內執行。</summary>
    public string Attribution { get; set; } = "capability";
}

public class ResourceScore
{
    public int Score { get; set; }
    public string Label { get; set; } = "";
    public decimal CpuPct { get; set; }
    public decimal MemPct { get; set; }
}
