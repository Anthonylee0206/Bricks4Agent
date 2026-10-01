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
        if (_containerMgr != null)
        {
            try { stats = await _containerMgr.GetStatsAsync(ct); }
            catch { /* container runtime 不在 → resource 分量會被略過 */ }
        }

        // 過去 15 min 每個 capability 的執行成功/失敗計數（execution_requests 是 PEP 一定會寫的表）
        var since = now.AddMinutes(-15);
        var statsByCapability = QueryExecutionStatsByCapability(since);

        var workerScores = new List<WorkerHealthScore>();
        foreach (var w in workers)
        {
            var hb = ScoreHeartbeat(now - w.LastHeartbeat, _heartbeatTimeout);
            var dispatch = ScoreDispatch(SumForCapabilities(statsByCapability, w.Capabilities));
            var resource = ScoreResource(stats, w.WorkerId);

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

    private static ResourceScore? ScoreResource(IReadOnlyList<ContainerStats>? stats, string workerId)
    {
        if (stats == null) return null;
        // worker_id 跟 container_name 不一定完全對應；用「container_name 包含 worker_id 第一段」做寬鬆匹配
        var prefix = workerId.Split('-')[0];   // e.g. trading-wkr-xxx → "trading"
        var s = stats.FirstOrDefault(x =>
            (x.ContainerName ?? "").Contains(prefix, StringComparison.OrdinalIgnoreCase));
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
