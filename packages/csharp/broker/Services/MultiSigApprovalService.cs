using System.Text.Json;
using Broker.Models;
using BrokerCore;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;

namespace Broker.Services;

/// <summary>
/// I1（minimal）— Multi-sig 裝飾器：是 4 層裝飾鏈中最外層。
///
/// 完整鏈：
///   PoolDispatcher
///     → MultiSigApprovalService     (I1 — 攔截 Approve、看是不是要 N-of-M)
///       → TimeAwareApprovalService  (H2 — 時段檢查)
///         → TemplateAwareApprovalService (H3 — 自動套用 template)
///           → TradingApprovalService       (Benson 原作)
///
/// Approve 流程：
/// 1. 寫一筆 ApprovalDecisionRecord (approver_pid, approved)
/// 2. 查 multi_sig_rule for capability：若無 / min_approvers <= 1 → 直接呼 inner.Approve、回 true
/// 3. 數同 approval_id 已 approved 的決定數 ≥ min → 呼 inner.Approve、寫 audit "MULTI_SIG_THRESHOLD_REACHED"
/// 4. 否則 → 不放行、回 true（已記錄、但 status 仍 pending）
///
/// Reject 流程：任一 admin reject 立刻終止整單、呼 inner.Reject。
///
/// 重複簽：同 approver 對同一 approval_id 第二次 approve = 不增加計數（idempotent）、回 true。
/// </summary>
public class MultiSigApprovalService : ITradingApprovalService
{
    private readonly ITradingApprovalService _inner;
    private readonly BrokerDb _db;
    private readonly IAuditService _audit;
    private readonly ILogger<MultiSigApprovalService> _logger;

    public MultiSigApprovalService(ITradingApprovalService inner, BrokerDb db,
        IAuditService audit, ILogger<MultiSigApprovalService> logger)
    {
        _inner = inner; _db = db; _audit = audit; _logger = logger;
    }

    public bool RequiresApproval(string capabilityId, string route)
        => _inner.RequiresApproval(capabilityId, route);

    public TradingApprovalRequest GetOrCreatePending(string traceId, string capabilityId, string route,
        string payload, string principalId, string role)
        => _inner.GetOrCreatePending(traceId, capabilityId, route, payload, principalId, role);

    public List<TradingApprovalRequest> List(string? status = null, int limit = 50) => _inner.List(status, limit);

    public TradingApprovalRequest? Get(string approvalId) => _inner.Get(approvalId);

    public bool Approve(string approvalId, string decidedBy, string? reason = null)
    {
        var existing = _inner.Get(approvalId);
        if (existing == null) return false;

        // 沒 rule 或 min<=1 → 走原 single-sig 流程
        var rule = _db.Get<MultiSigRule>(existing.CapabilityId);
        if (rule == null || !rule.Enabled || rule.MinApprovers <= 1)
            return _inner.Approve(approvalId, decidedBy, reason);

        // 查既有決定（同 approver 不重複計數）
        var existingDecisions = _db.Query<ApprovalDecisionRecord>(
            "SELECT * FROM approval_decisions WHERE approval_id = @aid ORDER BY decided_at ASC",
            new { aid = approvalId });

        var alreadyApprovedSamePid = existingDecisions
            .Any(d => d.ApproverPid.Equals(decidedBy, StringComparison.OrdinalIgnoreCase)
                   && d.Decision == "approved");
        if (!alreadyApprovedSamePid)
        {
            _db.Insert(new ApprovalDecisionRecord {
                DecisionId = IdGen.New("apdec"),
                ApprovalId = approvalId,
                ApproverPid = decidedBy,
                Decision = "approved",
                Reason = reason,
                DecidedAt = DateTime.UtcNow,
            });
        }

        // 重新數
        var approvedCount = _db.Query<ApprovalDecisionRecord>(
            "SELECT * FROM approval_decisions WHERE approval_id = @aid AND decision = 'approved'",
            new { aid = approvalId }).Count;

        if (approvedCount < rule.MinApprovers)
        {
            _logger.LogInformation("MultiSig: {Aid} {Cur}/{Min} approvers, holding pending",
                approvalId, approvedCount, rule.MinApprovers);
            // 還沒到門檻、回 true 表示 caller 的指令已記錄、但 record 維持 pending
            return true;
        }

        // 達門檻 → 呼原 Approve、寫 audit
        var ok = _inner.Approve(approvalId, decidedBy: $"multisig:{approvedCount}-of-{rule.MinApprovers}",
            reason: $"threshold reached ({approvedCount}/{rule.MinApprovers})");
        try
        {
            _audit.RecordEvent(existing.TraceId, "MULTI_SIG_THRESHOLD_REACHED",
                principalId: decidedBy,
                resourceRef: existing.CapabilityId,
                details: JsonSerializer.Serialize(new {
                    approval_id = approvalId,
                    capability_id = existing.CapabilityId,
                    threshold = rule.MinApprovers,
                    approvers = existingDecisions.Where(d => d.Decision == "approved").Select(d => d.ApproverPid).Append(decidedBy).Distinct(),
                    at = DateTime.UtcNow,
                }));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "MULTI_SIG audit write failed"); }
        return ok;
    }

    public bool Reject(string approvalId, string decidedBy, string? reason = null)
    {
        // multi-sig 規則下：任一 reject 立刻終止
        var existing = _inner.Get(approvalId);
        if (existing == null) return false;
        try
        {
            _db.Insert(new ApprovalDecisionRecord {
                DecisionId = IdGen.New("apdec"),
                ApprovalId = approvalId,
                ApproverPid = decidedBy,
                Decision = "rejected",
                Reason = reason,
                DecidedAt = DateTime.UtcNow,
            });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "decision record insert failed"); }
        return _inner.Reject(approvalId, decidedBy, reason);
    }

    public bool MarkDispatched(string approvalId, string dispatchedBy)
        => _inner.MarkDispatched(approvalId, dispatchedBy);
}
