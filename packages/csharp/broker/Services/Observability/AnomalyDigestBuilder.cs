using System.Text.Json;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;

namespace Broker.Services.Observability;

/// <summary>
/// 從各資料來源收集一個時間窗的資料，交給 <see cref="AnomalyRules"/> 判定。
///
/// 和 v1 最大的不同：這裡對整個時間窗做統計（SQL 聚合），不是抽最新幾十筆給 LLM 看。
/// 每個來源各自 try/catch，讀取失敗會記在 <see cref="IncidentWindowData.SourceErrors"/>，
/// 報告上會寫出「這部分沒看到」，而不是默默當成沒有異常。
/// 不讀任何使用者對話內容：LINE 互動只取 ID、通道與「有沒有出錯」，錯誤訊息本身也不讀。
/// </summary>
public sealed class AnomalyDigestBuilder
{
    private const int MaxSamplesPerCapability = 5;
    private const int ReasonBriefLength = 80;

    private readonly BrokerDb _db;
    private readonly IServiceProvider _services;
    private readonly HttpStatusMetrics _http;
    private readonly ILogger<AnomalyDigestBuilder> _logger;

    public AnomalyDigestBuilder(
        BrokerDb db,
        IServiceProvider services,
        HttpStatusMetrics http,
        ILogger<AnomalyDigestBuilder> logger)
    {
        _db = db;
        _services = services;
        _http = http;
        _logger = logger;
    }

    /// <param name="auditFloorEventId">
    /// 稽核事件從這個編號之後開始掃（主鍵範圍，不必整張表掃描）；0 表示從頭掃。
    /// </param>
    /// <param name="hostStartedAt">broker 這次啟動的時間。</param>
    public async Task<IncidentWindowData> BuildAsync(
        DateTime start, DateTime end, long auditFloorEventId, DateTime hostStartedAt, CancellationToken ct = default)
    {
        var data = new IncidentWindowData
        {
            WindowStart = start,
            WindowEnd = end,
            HostStartedAt = hostStartedAt,
        };

        Collect(data, "execution_requests", () => CollectExecutions(data));
        Collect(data, "approval_requests", () => CollectPendingApprovals(data));
        Collect(data, "audit_events", () => CollectPlanEvents(data, auditFloorEventId));
        Collect(data, "http_status_metrics", () => CollectHttp(data));
        Collect(data, "function_pool", () => CollectPool(data));
        await CollectHealthNowAsync(data, ct);
        Collect(data, "llm_metrics", () => CollectLlm(data));
        Collect(data, "line_interactions", () => CollectInteractionErrors(data));
        return data;
    }

    private void Collect(IncidentWindowData data, string source, Action collect)
    {
        try
        {
            collect();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Incident report: failed to read {Source}", source);
            data.SourceErrors.Add($"{source}（{ex.GetType().Name}）");
        }
    }

    private void CollectExecutions(IncidentWindowData d)
    {
        var rows = _db.Query<ExecutionAggRow>(@"
SELECT capability_id AS CapabilityId,
       COUNT(*) AS Total,
       SUM(CASE WHEN execution_state = @succeeded THEN 1 ELSE 0 END) AS Succeeded,
       SUM(CASE WHEN execution_state = @failed    THEN 1 ELSE 0 END) AS Failed,
       SUM(CASE WHEN execution_state = @denied    THEN 1 ELSE 0 END) AS Denied
FROM execution_requests
WHERE updated_at >= @start AND updated_at < @end
GROUP BY capability_id",
            new
            {
                start = d.WindowStart,
                end = d.WindowEnd,
                succeeded = (int)ExecutionState.Succeeded,
                failed = (int)ExecutionState.Failed,
                denied = (int)ExecutionState.Denied,
            });

        // 失敗與被拒的樣本：給報告當證據 ID，並統計主要的拒絕原因
        var samples = _db.Query<ExecutionSampleRow>(@"
SELECT request_id AS RequestId, capability_id AS CapabilityId,
       execution_state AS State, policy_reason AS PolicyReason
FROM execution_requests
WHERE updated_at >= @start AND updated_at < @end
  AND execution_state IN (@failed, @denied)
ORDER BY updated_at DESC
LIMIT 500",
            new
            {
                start = d.WindowStart,
                end = d.WindowEnd,
                failed = (int)ExecutionState.Failed,
                denied = (int)ExecutionState.Denied,
            });
        var samplesByCapability = samples
            .GroupBy(s => s.CapabilityId ?? "", StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        d.ExecutionsScanned = rows.Sum(r => r.Total);
        foreach (var row in rows)
        {
            var capability = row.CapabilityId ?? "";
            var capabilitySamples = samplesByCapability.TryGetValue(capability, out var list) ? list : [];
            var ids = capabilitySamples
                .Select(s => s.RequestId)
                .OfType<string>()
                .Take(MaxSamplesPerCapability)
                .ToList();
            var reasons = capabilitySamples
                .Where(s => s.State == (int)ExecutionState.Denied && !string.IsNullOrWhiteSpace(s.PolicyReason))
                .GroupBy(s => Brief(s.PolicyReason!, ReasonBriefLength), StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Take(3)
                .Select(g => $"{g.Key}（{g.Count()}）")
                .ToList();
            d.Executions.Add(new CapabilityExecutionStats(capability, row.Succeeded, row.Failed, row.Denied, ids, reasons));
        }
    }

    private void CollectPendingApprovals(IncidentWindowData d)
    {
        var pending = _db.Query<ApprovalRequest>(
            "SELECT * FROM approval_requests WHERE status = @pending",
            new { pending = (int)ApprovalStatus.Pending });
        d.PendingApprovals = pending
            .Select(a => new PendingApprovalInfo(
                a.ApprovalId,
                a.CapabilityId,
                AsUtc(a.CreatedAt),
                a.ExpiresAt == default ? null : AsUtc(a.ExpiresAt)))
            .ToList();
    }

    private void CollectPlanEvents(IncidentWindowData d, long floor)
    {
        var window = new { floor, start = d.WindowStart, end = d.WindowEnd };
        var scan = _db.Query<AuditScanRow>(@"
SELECT COUNT(*) AS Cnt, COALESCE(MAX(event_id), 0) AS MaxId
FROM audit_events
WHERE event_id > @floor AND occurred_at >= @start AND occurred_at < @end", window).FirstOrDefault();
        d.AuditScanned = scan?.Cnt ?? 0;
        d.AuditMaxEventId = scan?.MaxId ?? 0;

        d.PlanEvents = _db.Query<AuditCountRow>(@"
SELECT event_type AS EventType, COUNT(*) AS Cnt, MAX(trace_id) AS SampleTraceId
FROM audit_events
WHERE event_id > @floor AND occurred_at >= @start AND occurred_at < @end
  AND event_type IN ('PLAN_FAILED', 'PLAN_DEADLOCKED', 'NODE_FAILED')
GROUP BY event_type", window)
            .Select(r => new AuditEventCount(r.EventType ?? "", r.Cnt, r.SampleTraceId))
            .ToList();
    }

    private void CollectHttp(IncidentWindowData d)
    {
        d.HttpMetricsAvailable = true;
        d.HttpMetricsStartedAt = _http.StartedAt;
        d.HttpErrors = _http.QueryErrors(d.WindowStart, d.WindowEnd);
        d.HttpResponsesTotal = _http.CountResponses(d.WindowStart, d.WindowEnd);
        d.HttpDroppedErrors = _http.DroppedErrors;
    }

    /// <summary>Function Pool 關閉時 HealthScoreService 不會註冊，worker 相關規則就標為不適用。</summary>
    private void CollectPool(IncidentWindowData d)
    {
        d.PoolEnabled = _services.GetService<HealthScoreService>() is not null;
        if (!d.PoolEnabled)
            return;

        d.SnapshotInterval = HealthScoreSnapshotService.TickInterval;
        var window = new { start = d.WindowStart, end = d.WindowEnd };

        d.HeartbeatLost = _db.Query<ObservationEvent>(@"
SELECT * FROM observation_events
WHERE event_type = 'WORKER_HEARTBEAT_LOST' AND observed_at >= @start AND observed_at < @end
ORDER BY observed_at", window)
            .Select(o => new HeartbeatLostInfo(o.WorkerId ?? "(unknown)", AsUtc(o.ObservedAt)))
            .ToList();

        d.Snapshots = _db.Query<HealthScoreSnapshot>(@"
SELECT * FROM health_score_snapshots
WHERE captured_at >= @start AND captured_at < @end
ORDER BY captured_at", window)
            .Select(s => new HealthPoint(AsUtc(s.CapturedAt), s.OverallScore, s.OverallStatus, s.WorkerCount))
            .ToList();

        var latest = _db.Query<HealthScoreSnapshot>(
            "SELECT * FROM health_score_snapshots ORDER BY captured_at DESC LIMIT 1", new { }).FirstOrDefault();
        d.LatestSnapshotAt = latest is null ? null : AsUtc(latest.CapturedAt);
    }

    private async Task CollectHealthNowAsync(IncidentWindowData d, CancellationToken ct)
    {
        var health = _services.GetService<HealthScoreService>();
        if (health is null)
            return;

        try
        {
            var report = await health.ComputeAsync(ct);
            d.Health = new HealthNow(report.OverallScore, report.OverallStatus, report.WorkerCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Incident report: failed to compute the current health score");
            d.SourceErrors.Add($"health_score（{ex.GetType().Name}）");
        }
    }

    private void CollectLlm(IncidentWindowData d)
    {
        var metrics = _services.GetService<LlmProxyMetrics>();
        if (metrics is null)
            return;

        var recent = metrics.Recent(LlmProxyMetrics.MaxRecentEntries);
        // 排除報告自己的敘述呼叫：否則敘述失敗會在下一份報告觸發 R9，變成自己製造的雜訊
        var inWindow = recent
            .Where(r => r.Ts >= d.WindowStart && r.Ts < d.WindowEnd)
            .Where(r => !string.Equals(r.TaskType, IncidentNarrator.TaskType, StringComparison.Ordinal))
            .ToList();
        var truncated = recent.Count >= LlmProxyMetrics.MaxRecentEntries && recent.Min(r => r.Ts) > d.WindowStart;
        d.Llm = new LlmWindowStats(inWindow.Count, inWindow.Count(r => !r.Success), metrics.StartedAt, truncated);
    }

    private void CollectInteractionErrors(IncidentWindowData d)
    {
        var entries = _db.Query<SharedContextEntry>(@"
SELECT * FROM shared_context_entries
WHERE key LIKE 'hlm.log.entry.%' AND created_at >= @start AND created_at < @end",
            new { start = d.WindowStart, end = d.WindowEnd });
        d.InteractionsScanned = entries.Count;

        foreach (var entry in entries)
        {
            HighLevelInteractionRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<HighLevelInteractionRecord>(entry.ContentRef);
            }
            catch (JsonException)
            {
                continue;
            }

            if (record is null || string.IsNullOrWhiteSpace(record.Error))
                continue;

            // 只記「出錯了」與互動 ID，不讀錯誤訊息本身（可能夾帶使用者輸入）
            d.InteractionErrors.Add(new InteractionErrorInfo(
                record.InteractionId,
                record.Channel,
                record.OccurredAt.UtcDateTime));
        }
    }

    /// <summary>SQLite 讀回的時間沒有時區（Kind=Unspecified）；寫入時一律是 UTC，這裡補回去。</summary>
    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Brief(string text, int max)
    {
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    private sealed class ExecutionAggRow
    {
        public string? CapabilityId { get; set; }
        public int Total { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int Denied { get; set; }
    }

    private sealed class ExecutionSampleRow
    {
        public string? RequestId { get; set; }
        public string? CapabilityId { get; set; }
        public int State { get; set; }
        public string? PolicyReason { get; set; }
    }

    private sealed class AuditScanRow
    {
        public int Cnt { get; set; }
        public long MaxId { get; set; }
    }

    private sealed class AuditCountRow
    {
        public string? EventType { get; set; }
        public int Cnt { get; set; }
        public string? SampleTraceId { get; set; }
    }
}
