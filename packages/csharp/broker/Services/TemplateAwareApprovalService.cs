using System.Text.Json;
using Broker.Models;
using BrokerCore.Models;
using BrokerCore.Services;

namespace Broker.Services;

/// <summary>
/// H3 — ITradingApprovalService 裝飾器：先讓 Benson 的 TradingApprovalService 正常寫一筆 pending、
/// 然後 ApprovalTemplateMatcher 比對；若命中 enabled template 就立刻 Approve()、
/// 並寫 AUTO_APPROVED_BY_TEMPLATE 進 audit_events（hash chain 防被改）。
///
/// 不繞過原 service：pending 那一筆永遠寫得進去、admin 在 dashboard 看到的是「approved
/// by template:XXX」的紀錄、有完整事件鏈、不是「神秘消失的 approval」。
///
/// 不命中 / 解析失敗 / template 全 disabled → 完全走原流程、不影響行為。
/// </summary>
public class TemplateAwareApprovalService : ITradingApprovalService
{
    private readonly ITradingApprovalService _inner;
    private readonly ApprovalTemplateMatcher _matcher;
    private readonly IAuditService _audit;
    private readonly ILogger<TemplateAwareApprovalService> _logger;

    public TemplateAwareApprovalService(ITradingApprovalService inner, ApprovalTemplateMatcher matcher,
        IAuditService audit, ILogger<TemplateAwareApprovalService> logger)
    {
        _inner = inner; _matcher = matcher; _audit = audit; _logger = logger;
    }

    public bool RequiresApproval(string capabilityId, string route)
        => _inner.RequiresApproval(capabilityId, route);

    public TradingApprovalRequest GetOrCreatePending(string traceId, string capabilityId, string route,
        string payload, string principalId, string role)
    {
        var record = _inner.GetOrCreatePending(traceId, capabilityId, route, payload, principalId, role);

        // 既存非 pending 紀錄不重審（idempotent）
        if (!string.Equals(record.Status, "pending", StringComparison.OrdinalIgnoreCase)) return record;

        ApprovalTemplate? matched;
        try { matched = _matcher.FindMatch(capabilityId, route, payload); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Template match crashed, falling back to manual pending");
            return record;
        }
        if (matched == null) return record;

        var decidedBy = $"template:{matched.TemplateId}";
        var ok = _inner.Approve(record.ApprovalId, decidedBy,
            reason: $"auto-approved by template '{matched.Description ?? matched.TemplateId}'");
        if (!ok)
        {
            _logger.LogWarning("Template matched but Approve() returned false (race?), approval_id={Id}", record.ApprovalId);
            return record;
        }
        record.Status = "approved";
        record.DecidedBy = decidedBy;
        record.DecidedAt = DateTime.UtcNow;
        record.DecisionReason = $"auto-approved by template '{matched.TemplateId}'";

        try
        {
            _audit.RecordEvent(traceId, "AUTO_APPROVED_BY_TEMPLATE",
                principalId: principalId,
                resourceRef: matched.TemplateId,
                details: JsonSerializer.Serialize(new {
                    template_id = matched.TemplateId,
                    description = matched.Description,
                    capability_id = capabilityId,
                    route,
                    approval_id = record.ApprovalId,
                    at = DateTime.UtcNow,
                }));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "AUTO_APPROVED audit write failed"); }

        _logger.LogInformation("Auto-approved approval={Aid} by template={Tid} cap={Cap} route={Route}",
            record.ApprovalId, matched.TemplateId, capabilityId, route);
        return record;
    }

    public List<TradingApprovalRequest> List(string? status = null, int limit = 50) => _inner.List(status, limit);
    public TradingApprovalRequest? Get(string approvalId) => _inner.Get(approvalId);
    public bool Approve(string approvalId, string decidedBy, string? reason = null)
        => _inner.Approve(approvalId, decidedBy, reason);
    public bool Reject(string approvalId, string decidedBy, string? reason = null)
        => _inner.Reject(approvalId, decidedBy, reason);
    public bool MarkDispatched(string approvalId, string dispatchedBy)
        => _inner.MarkDispatched(approvalId, dispatchedBy);
}
