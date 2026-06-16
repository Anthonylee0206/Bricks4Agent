using BaseOrm;

namespace Broker.Models;

/// <summary>
/// I1 — 個別 approver 的簽核紀錄。N-of-M multi-sig 用 COUNT 統計。
///
/// 為什麼跟 TradingApprovalRequest 分開：原表只記「最後決定」（decided_by 一個欄位）、
/// multi-sig 需要保留「每個簽核人各自的決定」、必須獨立表才支援查 audit / 撤回 / 替換。
///
/// 同 (approval_id, approver_pid) 唯一 — 一人對同一單只能簽一次。
/// </summary>
[Table("approval_decisions")]
public class ApprovalDecisionRecord
{
    [Key(AutoIncrement = false), MaxLength(64)]
    [Column("decision_id")]
    public string DecisionId { get; set; } = string.Empty;

    [Column("approval_id"), MaxLength(64), Required]
    public string ApprovalId { get; set; } = string.Empty;

    [Column("approver_pid"), MaxLength(64), Required]
    public string ApproverPid { get; set; } = string.Empty;

    /// <summary>"approved" / "rejected"</summary>
    [Column("decision"), MaxLength(20), Required]
    public string Decision { get; set; } = string.Empty;

    [Column("reason"), MaxLength(500)]
    public string? Reason { get; set; }

    [Column("decided_at")]
    public DateTime DecidedAt { get; set; } = DateTime.UtcNow;
}
