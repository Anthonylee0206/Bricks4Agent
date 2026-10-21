namespace Broker.Services.Observability;

/// <summary>
/// 異常偵測報告的設定，對應 appsettings 的 <c>Observability:IncidentReport</c>。
/// 預設不啟動排程；管理員手動觸發不受 <see cref="Enabled"/> 影響。
/// </summary>
public sealed class IncidentReportOptions
{
    public const string SectionName = "Observability:IncidentReport";

    /// <summary>是否啟動排程。關閉時只能由管理員手動觸發。</summary>
    public bool Enabled { get; set; }

    /// <summary>排程間隔（分鐘）。時間窗等於間隔，前後兩窗首尾相接、不留空窗。</summary>
    public int IntervalMinutes { get; set; } = 15;

    /// <summary>有規則觸發時，是否請 LLM 把規則結果寫成敘述。</summary>
    public bool Narrate { get; set; } = true;

    /// <summary>基線取前幾份「排程」報告（手動報告的時間窗長短不一，不拿來當基準）。</summary>
    public int BaselineReports { get; set; } = 4;

    public IncidentRuleThresholds Thresholds { get; set; } = new();
}

/// <summary>各規則的門檻，數值對應設計文件第 4 節。</summary>
public sealed class IncidentRuleThresholds
{
    /// <summary>R2：同一能力至少幾筆失敗才算。</summary>
    public int ExecutionFailedMin { get; set; } = 3;

    /// <summary>R2：失敗率達到多少算 Warning。</summary>
    public double ExecutionFailedRate { get; set; } = 0.2;

    /// <summary>R2：失敗率達到多少算 Critical。</summary>
    public double ExecutionFailedCriticalRate { get; set; } = 0.5;

    /// <summary>R3：同一能力至少幾筆拒絕才算。</summary>
    public int DeniedMin { get; set; } = 5;

    /// <summary>R3、R7、R8：超過基線幾倍才算暴增。</summary>
    public double SpikeFactor { get; set; } = 3.0;

    /// <summary>R4：審批等待超過幾分鐘算卡住。</summary>
    public int ApprovalPendingMinutes { get; set; } = 30;

    /// <summary>R7：同一路徑至少幾筆 4xx 才算。</summary>
    public int ClientErrorMinPerPath { get; set; } = 10;

    /// <summary>R8：401、403 達到幾筆就算，不看基線。</summary>
    public int AuthFailureMin { get; set; } = 20;

    /// <summary>R8：有基線時，至少幾筆、且超過基線 <see cref="SpikeFactor"/> 倍也算。</summary>
    public int AuthFailureSpikeFloor { get; set; } = 5;

    /// <summary>R9：至少幾次 LLM 呼叫失敗才算。</summary>
    public int LlmFailureMin { get; set; } = 3;

    /// <summary>R9：LLM 失敗率達到多少才算。</summary>
    public double LlmFailureRate { get; set; } = 0.3;

    /// <summary>R11：時間窗內健康分數掉幾分算下降。</summary>
    public int HealthScoreDrop { get; set; } = 30;

    /// <summary>R12：最新健康快照超過幾個快照間隔沒更新算中斷。</summary>
    public int SnapshotStaleIntervals { get; set; } = 3;
}
