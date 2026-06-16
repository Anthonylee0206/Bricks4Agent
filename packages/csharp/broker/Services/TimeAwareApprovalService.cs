using BrokerCore.Models;
using BrokerCore.Services;

namespace Broker.Services;

/// <summary>
/// H2 — ITradingApprovalService 裝飾器：時段外強制 require_approval、覆蓋原 ACL 預設。
///
/// 包在 TemplateAwareApprovalService（H3）外層、整條鏈：
///   PoolDispatcher
///     → TimeAwareApprovalService（H2 — 時段檢查）
///       → TemplateAwareApprovalService（H3 — 自動套用 template）
///         → ApprovalService（Benson 原作 — 寫 DB + hardcoded list）
///
/// 為什麼三層：每層職責單一、可獨立 disable / 測試 / 換實作。
/// </summary>
public class TimeAwareApprovalService : ITradingApprovalService
{
    private readonly ITradingApprovalService _inner;
    private readonly TimeAclService _timeAcl;
    private readonly ILogger<TimeAwareApprovalService> _logger;

    public TimeAwareApprovalService(ITradingApprovalService inner, TimeAclService timeAcl,
        ILogger<TimeAwareApprovalService> logger)
    {
        _inner = inner; _timeAcl = timeAcl; _logger = logger;
    }

    public bool RequiresApproval(string capabilityId, string route)
    {
        // 原本就需要 approval → 維持
        if (_inner.RequiresApproval(capabilityId, route)) return true;

        // 沒原 require、看時段：在窗內 → 跟原意一致 false；窗外 → 強制 true
        try
        {
            var inside = _timeAcl.IsInsideAutoWindow(capabilityId, DateTime.UtcNow);
            if (inside == false)
            {
                _logger.LogInformation("Time ACL: {Cap} forced require_approval (off auto window)", capabilityId);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Time ACL crashed, falling back to original ACL");
        }
        return false;
    }

    public TradingApprovalRequest GetOrCreatePending(string traceId, string capabilityId, string route,
        string payload, string principalId, string role)
        => _inner.GetOrCreatePending(traceId, capabilityId, route, payload, principalId, role);

    public List<TradingApprovalRequest> List(string? status = null, int limit = 50) => _inner.List(status, limit);
    public TradingApprovalRequest? Get(string approvalId) => _inner.Get(approvalId);
    public bool Approve(string approvalId, string decidedBy, string? reason = null)
        => _inner.Approve(approvalId, decidedBy, reason);
    public bool Reject(string approvalId, string decidedBy, string? reason = null)
        => _inner.Reject(approvalId, decidedBy, reason);
    public bool MarkDispatched(string approvalId, string dispatchedBy)
        => _inner.MarkDispatched(approvalId, dispatchedBy);
}
