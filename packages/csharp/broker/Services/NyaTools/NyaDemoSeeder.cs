using BrokerCore;
using BrokerCore.Data;
using BrokerCore.Models;

namespace Broker.Services.NyaTools;

/// <summary>
/// Doc 1 demo 開關（config-gated，預設關）。把設定的 demo 使用者綁到一個帶 <c>role_admin</c>
/// 的 Principal，讓 <see cref="BrokerToolAuthorizer"/> 的權限閘對該使用者放行 ——
/// 用來人工測試 <c>dispatch_research_agent</c> 等需權限的工具，不必先做多階 role 管理 UI。
/// </summary>
/// <remarks>
/// <para><b>這只是 demo 開關，不是治理機制。</b>未來的多階 role 管理是另一回事（spec §5 的 seam）。</para>
/// <para>安全界線：<c>role_admin</c> 是 <c>["*"]</c>（全開），故 demo 使用者**通過所有工具的「權限閘」**。
/// 但權限閘只是三閘之一 —— 危險動作仍需 (a) 工具本身被啟用（<c>NyaToolEnableStore</c>，預設停用）
/// 與 (b) <c>執行</c>/審批閘。故全開僅讓「查詢/派遣」這類工具可被該 demo 使用者觸發，
/// 不等於可無審批地下單。正式環境請勿啟用本開關、改用真正的 role 指派。</para>
/// <para>冪等：三筆（Principal / role_binding / nya binding）都先查再補，重啟不會重複。可逆：關掉 flag 即停
/// （已建的列需手動清，或砍 broker.db 重建）。</para>
/// </remarks>
public static class NyaDemoSeeder
{
    public static void Seed(
        BrokerDb db,
        NyaPrincipalBindingService bindings,
        NyaDemoSeedOptions options,
        ILogger logger)
    {
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.UserId))
        {
            logger.LogInformation("[NyaDemoSeed] disabled or no UserId configured; skipping.");
            return;
        }

        var principalId = string.IsNullOrWhiteSpace(options.PrincipalId) ? "prn_nyademo" : options.PrincipalId;
        var roleId = string.IsNullOrWhiteSpace(options.RoleId) ? "role_admin" : options.RoleId;
        var channel = string.IsNullOrWhiteSpace(options.Channel) ? "line" : options.Channel;

        // ① Principal（Active）。
        if (db.Get<Principal>(principalId) == null)
            db.Insert(new Principal
            {
                PrincipalId = principalId,
                ActorType = ActorType.Human,
                DisplayName = string.IsNullOrWhiteSpace(options.DisplayName) ? "Nya Demo (admin)" : options.DisplayName,
                Status = EntityStatus.Active,
                CreatedAt = DateTime.UtcNow
            });

        // ② role_binding → roleId（冪等：已有 Active 綁定就跳過）。
        var hasRole = db.Query<RoleBinding>(
            "SELECT * FROM role_bindings WHERE principal_id = @p AND role_id = @r AND status = @a",
            new { p = principalId, r = roleId, a = (int)EntityStatus.Active }).Count > 0;
        if (!hasRole)
            db.Insert(new RoleBinding
            {
                BindingId = IdGen.New("rb"),
                PrincipalId = principalId,
                RoleId = roleId,
                GrantedBy = "nya-demo-seed",
                GrantedAt = DateTime.UtcNow,
                Status = EntityStatus.Active
            });

        // ③ nya binding（冪等：已解析到同 principal 就跳過）。
        if (!string.Equals(bindings.Resolve(options.UserId, channel), principalId, StringComparison.Ordinal))
            bindings.Bind(options.UserId, channel, principalId, "nya-demo-seed");

        logger.LogWarning(
            "[NyaDemoSeed] DEMO GRANT active: {User}/{Channel} → {Principal} → {Role} (full-open permission gate; " +
            "tool-enable + 執行/approval gates still apply). Do NOT enable in production.",
            options.UserId, channel, principalId, roleId);
    }
}

/// <summary>Doc 1 demo 開關設定（appsettings: <c>NyaDemoSeed</c>）。預設關；真實 UserId 放本地 gitignore override。</summary>
public sealed class NyaDemoSeedOptions
{
    /// <summary>是否啟用 demo 授予（預設 false；正式環境保持 false）。</summary>
    public bool Enabled { get; set; }

    /// <summary>要授權的 Nya 使用者識別（跨通道正規化後的 userId）。空 = 不授予。</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>來源通道（line / discord / web…），預設 line。</summary>
    public string Channel { get; set; } = "line";

    /// <summary>綁定的 Principal id（預設 prn_nyademo）。</summary>
    public string PrincipalId { get; set; } = "prn_nyademo";

    /// <summary>授予的角色（預設 role_admin = 全開）。</summary>
    public string RoleId { get; set; } = "role_admin";

    public string DisplayName { get; set; } = "Nya Demo (admin)";
}
