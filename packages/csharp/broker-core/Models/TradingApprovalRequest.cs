using BaseOrm;

namespace BrokerCore.Models;

/// <summary>
/// 待人工核准的 capability 派發請求。
///
/// 工作流程：
///   1. PoolDispatcher 收到一個被 ApprovalPolicy 標為 require_approval 的 capability
///   2. 寫一筆 status='pending' 進來、回呼叫者 Fail("approval_id=...")
///   3. Admin 在 dashboard 看待審清單、按 approve / reject
///   4. 呼叫者 retry 同一個 trace_id（或重 dispatch）→ PoolDispatcher 看到 approved 直接放行；
///      看到 rejected 回 Fail
///
/// 設計選擇 — 為什麼不阻塞等待：HTTP request 阻塞等 admin 點按鈕在 SaaS 場景不可行。
/// 改成「先回 pending、caller 自己 retry / 訂閱通知」可以撐到任何審核時長。
///
/// 列為 audit 鏈的補強——本表是核准 metadata、底層派發過程的審計仍然在 audit_events。
/// </summary>
[Table("trading_approval_requests")]
public class TradingApprovalRequest
{
    // [Key] 預設 AutoIncrement=true、SQLite 會強行做 INTEGER PRIMARY KEY、把 IdGen.New("apr")
    // 產的 string 吃掉換成 int 自動編號（split-brain：記憶體跑 string、DB 存 int）。
    // 跟其他 string-key model（Principal 等）對齊、明寫 AutoIncrement=false。
    [Key(AutoIncrement = false), MaxLength(64)]
    [Column("approval_id")]
    public string ApprovalId { get; set; } = string.Empty;

    /// <summary>關聯的 trace_id（同一條派發鏈、approve 後 caller 用同 trace_id 重派）</summary>
    [Column("trace_id")]
    [MaxLength(64)]
    [Required]
    public string TraceId { get; set; } = string.Empty;

    [Column("capability_id")]
    [MaxLength(80)]
    [Required]
    public string CapabilityId { get; set; } = string.Empty;

    [Column("route")]
    [MaxLength(80)]
    public string Route { get; set; } = string.Empty;

    /// <summary>原始 payload（JSON），admin 用來判斷批不批</summary>
    [Column("payload")]
    public string Payload { get; set; } = "{}";

    [Column("principal_id")]
    [MaxLength(64)]
    public string PrincipalId { get; set; } = string.Empty;

    [Column("role")]
    [MaxLength(40)]
    public string Role { get; set; } = string.Empty;

    [Column("requested_at")]
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    /// <summary>"pending" / "approved" / "rejected" / "expired"</summary>
    [Column("status")]
    [MaxLength(20)]
    [Required]
    public string Status { get; set; } = "pending";

    [Column("decided_by")]
    [MaxLength(64)]
    public string? DecidedBy { get; set; }

    [Column("decided_at")]
    public DateTime? DecidedAt { get; set; }

    [Column("decision_reason")]
    [MaxLength(500)]
    public string? DecisionReason { get; set; }

    /// <summary>
    /// 真正派發到 worker 的時間。null = 還沒派過、可派；非 null = 已派過、禁止再派。
    /// 為什麼要分開記、不直接看 status='approved'：approve-and-dispatch 是兩步、
    /// approve 寫進 DB 後 dispatch 可能失敗、admin 想 retry。但如果 admin 看到
    /// status='approved' 就能無限按「立刻執行」、每按一次就真下一單到交易所、損失真錢。
    /// 寫 DispatchedAt 之後 endpoint 會擋掉重複派發、admin 真要 retry 必須先排查清楚。
    /// </summary>
    [Column("dispatched_at")]
    public DateTime? DispatchedAt { get; set; }

    /// <summary>誰按下派發的（通常 = DecidedBy、但分開記允許多人協作 audit）</summary>
    [Column("dispatched_by")]
    [MaxLength(64)]
    public string? DispatchedBy { get; set; }
}
