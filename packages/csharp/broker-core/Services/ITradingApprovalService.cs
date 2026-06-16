using BrokerCore.Data;
using BrokerCore.Models;

namespace BrokerCore.Services;

/// <summary>
/// 核准服務——管理 trading_approval_requests 表 + 提供「這個 capability 需要 approve 嗎」的 policy。
///
/// PoolDispatcher 派發前呼叫 GetOrCreatePending() 看狀態：
///   - null → 不需要 approve、放行
///   - status='pending' → 寫了一筆等審、Fail caller
///   - status='approved' → 放行
///   - status='rejected' → Fail caller
/// </summary>
public interface ITradingApprovalService
{
    /// <summary>
    /// 查 (capability, route) 組合是否需要 approve。
    /// 兩種匹配：capability-level（整個 capability 都受控）或 capability::route 精確匹配。
    /// 後者用於同 capability 內讀寫分離（例 trading.perpetual 讀 OK、place_order/cancel_order 受控）。
    /// </summary>
    bool RequiresApproval(string capabilityId, string route);

    /// <summary>
    /// 對給定 trace_id 取得 / 建立 approval。
    /// 第一次呼叫產 'pending'；之後同 trace_id 直接回上次的 record。
    /// </summary>
    TradingApprovalRequest GetOrCreatePending(
        string traceId, string capabilityId, string route, string payload,
        string principalId, string role);

    /// <summary>列 approval（依 status 過濾、新到舊）</summary>
    List<TradingApprovalRequest> List(string? status = null, int limit = 50);

    /// <summary>查單一 approval</summary>
    TradingApprovalRequest? Get(string approvalId);

    /// <summary>標 approved</summary>
    bool Approve(string approvalId, string decidedBy, string? reason = null);

    /// <summary>標 rejected</summary>
    bool Reject(string approvalId, string decidedBy, string? reason = null);

    /// <summary>
    /// 標記為已派發（冪等鎖）。回 false 代表已被別人 set 過、caller 不該再派。
    /// 設計選擇：在 dispatch 前 set、避免 race / 多次 click 重複下單。
    /// 即使 dispatch 後續失敗也不 reset——admin 想 retry 必須手動排查、避免雙倒單。
    /// </summary>
    bool MarkDispatched(string approvalId, string dispatchedBy);
}

/// <summary>
/// 預設實作：policy 寫死、storage 走 BrokerDb。
///
/// 預設規則（KISS）：
///   trading.order → 需要 approve（實單下單；多用戶階段擋下手動下單 surface area）
///   其餘 → 直接放行
///
/// trading.perpetual / trading.account 不在內、AutoTrader 仍能照常跑。
/// 之後想加更多受控 capability 直接補進這個集合。
/// </summary>
public class TradingApprovalService : ITradingApprovalService
{
    private readonly BrokerDb _db;

    /// <summary>整個 capability 都受控（任何 route 都要 approve）</summary>
    private static readonly HashSet<string> RequiringApprovalCapabilities =
        new(StringComparer.OrdinalIgnoreCase) { "trading.order" };

    /// <summary>
    /// 特定 capability::route 受控；用於同 capability 內讀寫分離。
    /// 格式 "capability::route" 全 lowercase。
    /// </summary>
    private static readonly HashSet<string> RequiringApprovalRoutes =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "trading.perpetual::place_order",
        "trading.perpetual::cancel_order",
        "trading.perpetual::set_leverage",
    };

    /// <summary>
    /// 只讀 route 白名單(capability::route)——即使整個 capability 受控(如 trading.order),
    /// 這些「純查詢、不動錢」的 route 也放行,不洗審核閘。
    /// 安全預設:不在這裡的 route 一律照原規則受控(漏列只會多一個無害的審核彈窗、不會放過下單)。
    /// 起因:儀表板每幾秒 poll list_orders → 每次都生審核 → Discord 洗版。
    /// </summary>
    private static readonly HashSet<string> ReadOnlyExemptRoutes =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "trading.order::list_orders",
        "trading.order::get_order",
    };

    public TradingApprovalService(BrokerDb db) { _db = db; }

    public bool RequiresApproval(string capabilityId, string route)
    {
        // 只讀白名單優先:純查詢放行(place_order / cancel_order 等 write route 不在此、仍受控)
        if (ReadOnlyExemptRoutes.Contains($"{capabilityId}::{route}")) return false;
        if (RequiringApprovalCapabilities.Contains(capabilityId)) return true;
        var key = $"{capabilityId}::{route}";
        return RequiringApprovalRoutes.Contains(key);
    }

    public TradingApprovalRequest GetOrCreatePending(
        string traceId, string capabilityId, string route, string payload,
        string principalId, string role)
    {
        // 同 trace_id 已經有紀錄 → 回原本的（不重複建）
        var existing = _db.QueryFirst<TradingApprovalRequest>(
            "SELECT * FROM trading_approval_requests WHERE trace_id = @traceId LIMIT 1",
            new { traceId });
        if (existing != null) return existing;

        var req = new TradingApprovalRequest
        {
            ApprovalId = IdGen.New("apr"),
            TraceId = traceId,
            CapabilityId = capabilityId,
            Route = route,
            Payload = payload,
            PrincipalId = principalId,
            Role = role,
            RequestedAt = DateTime.UtcNow,
            Status = "pending",
        };
        _db.Insert(req);
        return req;
    }

    public List<TradingApprovalRequest> List(string? status = null, int limit = 50)
    {
        if (string.IsNullOrEmpty(status))
            return _db.Query<TradingApprovalRequest>(
                "SELECT * FROM trading_approval_requests ORDER BY requested_at DESC LIMIT @limit",
                new { limit });
        return _db.Query<TradingApprovalRequest>(
            "SELECT * FROM trading_approval_requests WHERE status = @status ORDER BY requested_at DESC LIMIT @limit",
            new { status, limit });
    }

    public TradingApprovalRequest? Get(string approvalId)
        => _db.QueryFirst<TradingApprovalRequest>(
            "SELECT * FROM trading_approval_requests WHERE approval_id = @id",
            new { id = approvalId });

    public bool Approve(string approvalId, string decidedBy, string? reason = null)
        => UpdateStatus(approvalId, "approved", decidedBy, reason);

    public bool Reject(string approvalId, string decidedBy, string? reason = null)
        => UpdateStatus(approvalId, "rejected", decidedBy, reason);

    private bool UpdateStatus(string approvalId, string newStatus, string decidedBy, string? reason)
    {
        var existing = Get(approvalId);
        if (existing == null) return false;
        if (existing.Status != "pending") return false;  // 已決定 → 不能重決
        existing.Status = newStatus;
        existing.DecidedBy = decidedBy;
        existing.DecidedAt = DateTime.UtcNow;
        existing.DecisionReason = reason;
        _db.Update(existing);
        return true;
    }

    public bool MarkDispatched(string approvalId, string dispatchedBy)
    {
        var existing = Get(approvalId);
        if (existing == null) return false;
        if (existing.DispatchedAt != null) return false;  // 已派過、冪等鎖：拒絕再派
        existing.DispatchedAt = DateTime.UtcNow;
        existing.DispatchedBy = dispatchedBy;
        _db.Update(existing);
        return true;
    }
}
