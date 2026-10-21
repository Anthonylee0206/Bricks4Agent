using System.Text.Json.Serialization;

namespace Broker.Services.Observability;

public static class IncidentSeverity
{
    public const string Warning = "warning";
    public const string Critical = "critical";
}

/// <summary>規則觸發的一筆異常。內容全部由規則產生，不經過 LLM。</summary>
public sealed class IncidentFinding
{
    [JsonPropertyName("rule_id")]
    public string RuleId { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = IncidentSeverity.Warning;

    /// <summary>異常的對象：能力、路徑、worker 等。</summary>
    [JsonPropertyName("subject")]
    public string Subject { get; set; } = "";

    /// <summary>本時間窗的數值（筆數、分數等，依規則而定）。</summary>
    [JsonPropertyName("value")]
    public double Value { get; set; }

    /// <summary>依基線換算出的預期值；沒有基線時為 null。</summary>
    [JsonPropertyName("baseline")]
    public double? Baseline { get; set; }

    /// <summary>規則寫的一句話說明。</summary>
    [JsonPropertyName("detail")]
    public string Detail { get; set; } = "";

    /// <summary>相關的 ID（最多 5 筆），方便追查。</summary>
    [JsonPropertyName("evidence")]
    public List<string> Evidence { get; set; } = new();
}

/// <summary>這份報告實際看了什麼。「沒看到」和「沒有異常」要分得開。</summary>
public sealed class IncidentCoverage
{
    /// <summary>每個資料來源在這個時間窗掃了幾筆。</summary>
    [JsonPropertyName("scanned")]
    public Dictionary<string, int> Scanned { get; set; } = new();

    [JsonPropertyName("rules_run")]
    public List<string> RulesRun { get; set; } = new();

    /// <summary>這次沒有執行的規則，以及原因。</summary>
    [JsonPropertyName("rules_not_applicable")]
    public Dictionary<string, string> RulesNotApplicable { get; set; } = new();

    /// <summary>資料本身的限制，例如記憶體統計在時間窗內歸零過、某個來源讀取失敗。</summary>
    [JsonPropertyName("notes")]
    public List<string> Notes { get; set; } = new();
}

public sealed class IncidentReport
{
    [JsonPropertyName("report_id")]
    public string ReportId { get; set; } = "";

    /// <summary>scheduled 或 manual。</summary>
    [JsonPropertyName("trigger")]
    public string Trigger { get; set; } = IncidentReportTriggers.Scheduled;

    [JsonPropertyName("window_start")]
    public DateTime WindowStart { get; set; }

    [JsonPropertyName("window_end")]
    public DateTime WindowEnd { get; set; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; set; }

    /// <summary>none、warning 或 critical。</summary>
    [JsonPropertyName("max_severity")]
    public string MaxSeverity { get; set; } = "none";

    [JsonPropertyName("findings")]
    public List<IncidentFinding> Findings { get; set; } = new();

    [JsonPropertyName("coverage")]
    public IncidentCoverage Coverage { get; set; } = new();

    [JsonPropertyName("blind_spots")]
    public List<string> BlindSpots { get; set; } = new();

    /// <summary>本時間窗的計數，供之後的排程報告當基線。</summary>
    [JsonPropertyName("metrics")]
    public Dictionary<string, double> Metrics { get; set; } = new();

    /// <summary>本時間窗看到的最大稽核事件編號；下一窗從這附近接著掃，不必整張表掃描。</summary>
    [JsonPropertyName("audit_watermark")]
    public long AuditWatermark { get; set; }

    [JsonPropertyName("narrative")]
    public string? Narrative { get; set; }

    /// <summary>skipped_no_findings、disabled、llm_unavailable、ok、incomplete 或 failed。</summary>
    [JsonPropertyName("narrative_status")]
    public string NarrativeStatus { get; set; } = "";

    [JsonPropertyName("narrative_model")]
    public string? NarrativeModel { get; set; }

    /// <summary>守門檢查的結果：敘述裡漏掉的規則代號。</summary>
    [JsonPropertyName("narrative_missing_rules")]
    public List<string> NarrativeMissingRules { get; set; } = new();

    [JsonPropertyName("duration_ms")]
    public long DurationMs { get; set; }
}

public static class IncidentReportTriggers
{
    public const string Scheduled = "scheduled";
    public const string Manual = "manual";
}

/// <summary>
/// 規則的輸入：一個時間窗內從各資料來源收集到的東西。
/// 刻意不放任何使用者對話內容（只有 ID、計數、錯誤摘要）。
/// </summary>
public sealed class IncidentWindowData
{
    public DateTime WindowStart { get; set; }
    public DateTime WindowEnd { get; set; }

    /// <summary>broker 這次啟動的時間；用來判斷「剛啟動、還沒有快照」這種情況。</summary>
    public DateTime HostStartedAt { get; set; }

    public List<CapabilityExecutionStats> Executions { get; set; } = new();
    public int ExecutionsScanned { get; set; }

    /// <summary>目前仍在等待的審批（不受時間窗限制：卡住就是卡住）。</summary>
    public List<PendingApprovalInfo> PendingApprovals { get; set; } = new();

    public List<AuditEventCount> PlanEvents { get; set; } = new();
    public int AuditScanned { get; set; }
    public long AuditMaxEventId { get; set; }

    public bool HttpMetricsAvailable { get; set; }
    public DateTime? HttpMetricsStartedAt { get; set; }
    public int HttpResponsesTotal { get; set; }

    /// <summary>HTTP 錯誤統計因為達到組數上限而漏記的筆數（broker 啟動以來）。</summary>
    public long HttpDroppedErrors { get; set; }

    /// <summary>狀態碼 ≥ 400 的回應，依正規化後的路徑與狀態碼分組。</summary>
    public List<HttpStatusCount> HttpErrors { get; set; } = new();

    /// <summary>Function Pool 是否啟用；沒啟用時 worker 相關規則不適用。</summary>
    public bool PoolEnabled { get; set; }

    public List<HeartbeatLostInfo> HeartbeatLost { get; set; } = new();

    /// <summary>報告產生當下即時計算的健康狀態。</summary>
    public HealthNow? Health { get; set; }

    public List<HealthPoint> Snapshots { get; set; } = new();
    public DateTime? LatestSnapshotAt { get; set; }
    public TimeSpan SnapshotInterval { get; set; }

    public LlmWindowStats? Llm { get; set; }

    public List<InteractionErrorInfo> InteractionErrors { get; set; } = new();
    public int InteractionsScanned { get; set; }

    /// <summary>讀取失敗的資料來源；會寫進報告的涵蓋範圍，不會被當成「沒有異常」。</summary>
    public List<string> SourceErrors { get; set; } = new();
}

public sealed record CapabilityExecutionStats(
    string CapabilityId,
    int Succeeded,
    int Failed,
    int Denied,
    IReadOnlyList<string> SampleRequestIds,
    IReadOnlyList<string> TopDenyReasons);

public sealed record PendingApprovalInfo(string ApprovalId, string CapabilityId, DateTime CreatedAt, DateTime? ExpiresAt);

public sealed record AuditEventCount(string EventType, int Count, string? SampleTraceId);

public sealed record HttpStatusCount(string Path, int StatusCode, int Count);

public sealed record HeartbeatLostInfo(string WorkerId, DateTime ObservedAt);

public sealed record HealthNow(int Score, string Status, int WorkerCount);

public sealed record HealthPoint(DateTime CapturedAt, int Score, string Status, int WorkerCount);

/// <param name="BufferMayBeTruncated">記憶體只保留最近的呼叫，時間窗較舊的部分可能已被擠掉。</param>
public sealed record LlmWindowStats(int Calls, int Failures, DateTime MetricsStartedAt, bool BufferMayBeTruncated);

/// <summary>
/// 出錯的對話互動。刻意不帶錯誤訊息：訊息可能夾帶使用者輸入（例如「專案名稱「…」已存在」），
/// 報告、告警頁與送給 LLM 的摘要都不該出現；管理員用互動 ID 到後台的對話紀錄查看。
/// </summary>
public sealed record InteractionErrorInfo(string InteractionId, string Channel, DateTime OccurredAt);
