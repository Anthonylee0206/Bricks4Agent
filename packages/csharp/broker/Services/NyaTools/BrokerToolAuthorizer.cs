using System.Text.Json;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;
using BrokerCore.Models;

namespace Broker.Services.NyaTools;

/// <summary>
/// broker 治理層提供的工具授權實作（大項四 4D；寄宿功課 Phase 0-B 填入實作）。
/// 覆蓋 Nya 內建的 AllowAllToolAuthorizer。
/// </summary>
/// <remarks>
/// 同時用於兩個時點（見 <see cref="INyaToolAuthorizer"/>）：
/// (1) 建構 LLM 工具清單時決定是否暴露；(2) 工具執行前再次檢查。
/// <para>
/// 判權鏈（每一環失敗即拒絕，安全預設）：
/// <c>RequiredPermission</c> 為 null/空 → 允許（現況 fact/search/transport 行為不變）→
/// 否則 Nya userId 經 <see cref="NyaPrincipalBindingService"/> 換成 PrincipalId →
/// principal 須 Active → 取未過期且 Active 的 <c>role_bindings</c> →
/// 對應 Active 的 <c>roles.default_capability_ids</c> 含該權限或 <c>"*"</c>。
/// </para>
/// <para>
/// 不快取：撤銷（Nya 綁定或 role binding）必須在進行中對話的下一個工具呼叫立即生效。
/// 執行時點的拒絕經 Orchestrator 既有路徑寫入 nya_audit_log（tool_result/error + denyReason）；
/// 暴露時點的拒絕僅記 server log（每則訊息都會掃全工具清單，寫稽核會灌爆）。
/// </para>
/// </remarks>
public sealed class BrokerToolAuthorizer : INyaToolAuthorizer
{
    private readonly BrokerDb _db;
    private readonly NyaPrincipalBindingService _bindings;
    private readonly ILogger<BrokerToolAuthorizer> _logger;

    public BrokerToolAuthorizer(
        BrokerDb db,
        NyaPrincipalBindingService bindings,
        ILogger<BrokerToolAuthorizer> logger)
    {
        _db = db;
        _bindings = bindings;
        _logger = logger;
    }

    public bool CanUse(string userId, string channelType, NyaToolSchema tool)
    {
        if (string.IsNullOrWhiteSpace(tool.RequiredPermission))
            return true;

        var allowed = HasPermission(userId, channelType, tool.RequiredPermission);
        if (!allowed)
            _logger.LogInformation(
                "[BrokerToolAuthorizer] Denied tool {Tool} (perm={Perm}) for user {User}/{Channel}",
                tool.Name, tool.RequiredPermission, userId, channelType);
        return allowed;
    }

    /// <summary>
    /// 權限判定：使用者的有效角色（<see cref="ActiveRoles"/>）之能力清單含該權限。
    /// 任何治理資料缺失（表不存在 / JSON 壞）一律視為拒絕，並記 server log。
    /// </summary>
    private bool HasPermission(string userId, string channelType, string requiredPermission)
    {
        try
        {
            return ActiveRoles(_db, _bindings, userId, channelType).Any(r => RoleGrants(r, requiredPermission));
        }
        catch (Exception ex)
        {
            // 治理查詢失敗 → 拒絕（fail-closed），原因只進 server log。
            _logger.LogWarning(ex,
                "[BrokerToolAuthorizer] Permission lookup failed for user {User}/{Channel} perm={Perm}; denying.",
                userId, channelType, requiredPermission);
            return false;
        }
    }

    /// <summary>
    /// 使用者目前的有效角色：Nya 綁定 → Principal Active → Active 且未過期（expires_at NULL = 永久）的
    /// role_bindings → Active 的 roles。工具判權用。
    /// </summary>
    internal static List<Role> ActiveRoles(BrokerDb db, NyaPrincipalBindingService bindings, string userId, string channelType)
    {
        var principalId = bindings.Resolve(userId, channelType);
        if (principalId == null)
            return new List<Role>();

        var principal = db.Get<Principal>(principalId);
        if (principal == null || principal.Status != EntityStatus.Active)
            return new List<Role>();

        return db.Query<RoleBinding>(
                @"SELECT * FROM role_bindings
                  WHERE principal_id = @principalId
                    AND status = @active
                    AND (expires_at IS NULL OR expires_at > @now)",
                new { principalId, active = (int)EntityStatus.Active, now = DateTime.UtcNow })
            .Select(rb => rb.RoleId)
            .Distinct()
            .Select(roleId => db.Get<Role>(roleId))
            .Where(role => role != null && role.Status == EntityStatus.Active)
            .Select(role => role!)
            .ToList();
    }

    /// <summary>角色的 default_capability_ids（JSON 陣列）是否含該權限或萬用 "*"。</summary>
    private bool RoleGrants(Role role, string requiredPermission)
    {
        try
        {
            var caps = JsonSerializer.Deserialize<string[]>(role.DefaultCapabilityIds) ?? Array.Empty<string>();
            return caps.Contains("*") ||
                   caps.Contains(requiredPermission, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex,
                "[BrokerToolAuthorizer] Role {Role} has malformed default_capability_ids; treating as no grants.",
                role.RoleId);
            return false;
        }
    }
}
