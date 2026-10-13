using System.Text.Json;
using BrokerCore;
using BrokerCore.Data;
using BrokerCore.Models;

namespace Broker.Services.NyaTools;

public record NyaRoleView(string RoleId, string DisplayName, string[] AllowedTaskTypes, int CapabilityCount, bool AllCapabilities, int ActiveBindingCount, bool BuiltIn);
public record NyaRoleDetail(string RoleId, string DisplayName, string[] AllowedTaskTypes, string[] CapabilityIds, bool AllCapabilities, int Version, bool BuiltIn);
public record NyaCapabilityView(string CapabilityId, string Route, string ResourceType, int RiskLevel, string ApprovalPolicy);
public record NyaUserRoleView(string RoleId, string DisplayName, int CapabilityCount, DateTime GrantedAt, string BindingId);
public record NyaUserRolesResult(string? PrincipalId, List<NyaUserRoleView> Roles);
public record NyaRoleInput(string RoleId, string DisplayName, string[] AllowedTaskTypes, string[] CapabilityIds);

/// <summary>
/// 身分組治理 UI 後端服務（items 1–2）。把「Nya 使用者 ↔ broker 角色」綁定與角色/能力 CRUD
/// 收斂於一處，端點僅薄包。綁定鏈：user → Principal（懶建）→ RoleBinding → Role。
/// </summary>
public sealed class NyaRoleAdminService
{
    private static readonly HashSet<string> BuiltInRoles = new(StringComparer.Ordinal)
        { "role_reader", "role_pm", "role_sa", "role_executor", "role_auditor", "role_admin" };

    private readonly BrokerDb _db;
    private readonly NyaPrincipalBindingService _bindings;
    private readonly ILogger<NyaRoleAdminService> _logger;
    private const string Channel = "*"; // admin UI 綁定固定全通道

    public NyaRoleAdminService(BrokerDb db, NyaPrincipalBindingService bindings, ILogger<NyaRoleAdminService> logger)
    {
        _db = db; _bindings = bindings; _logger = logger;
    }

    private static string[] ParseArr(string? json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private int ActiveBindingCount(string roleId) => _db.Query<RoleBinding>(
        "SELECT * FROM role_bindings WHERE role_id = @r AND status = @a",
        new { r = roleId, a = (int)EntityStatus.Active }).Count;

    public List<NyaRoleView> ListRoles()
        => _db.Query<Role>("SELECT * FROM roles WHERE status != @d", new { d = (int)EntityStatus.Deleted })
            .Select(r =>
            {
                var caps = ParseArr(r.DefaultCapabilityIds);
                return new NyaRoleView(r.RoleId, r.DisplayName, ParseArr(r.AllowedTaskTypes),
                    caps.Length, caps.Contains("*"), ActiveBindingCount(r.RoleId), BuiltInRoles.Contains(r.RoleId));
            }).ToList();

    public NyaRoleDetail? GetRole(string id)
    {
        var r = _db.Get<Role>(id);
        if (r == null || r.Status == EntityStatus.Deleted) return null;
        var caps = ParseArr(r.DefaultCapabilityIds);
        return new NyaRoleDetail(r.RoleId, r.DisplayName, ParseArr(r.AllowedTaskTypes), caps, caps.Contains("*"), r.Version, BuiltInRoles.Contains(r.RoleId));
    }

    public (bool ok, string? error) CreateRole(NyaRoleInput r)
    {
        if (string.IsNullOrWhiteSpace(r.RoleId) || !System.Text.RegularExpressions.Regex.IsMatch(r.RoleId, "^[A-Za-z0-9_-]+$"))
            return (false, "role_id 格式錯誤（限英數、_、-）");
        var existing = _db.Get<Role>(r.RoleId);
        if (existing != null)
        {
            if (existing.Status != EntityStatus.Deleted) return (false, "role_id 已存在");
            // 復活軟刪角色：重置欄位，version 回 1
            _db.Execute(
                "UPDATE roles SET display_name=@dn, allowed_task_types=@att, default_capability_ids=@dc, version=1, status=@active WHERE role_id=@id",
                new
                {
                    dn = r.DisplayName,
                    att = JsonSerializer.Serialize(r.AllowedTaskTypes ?? Array.Empty<string>()),
                    dc = JsonSerializer.Serialize(r.CapabilityIds ?? Array.Empty<string>()),
                    active = (int)EntityStatus.Active,
                    id = r.RoleId
                });
            _logger.LogInformation("[NyaRoleAdmin] Revived soft-deleted role {RoleId} ({DisplayName})", r.RoleId, r.DisplayName);
            return (true, null);
        }
        _db.Insert(new Role
        {
            RoleId = r.RoleId, DisplayName = r.DisplayName,
            AllowedTaskTypes = JsonSerializer.Serialize(r.AllowedTaskTypes ?? Array.Empty<string>()),
            DefaultCapabilityIds = JsonSerializer.Serialize(r.CapabilityIds ?? Array.Empty<string>()),
            Version = 1, Status = EntityStatus.Active
        });
        _logger.LogInformation("[NyaRoleAdmin] Created role {RoleId} ({DisplayName})", r.RoleId, r.DisplayName);
        return (true, null);
    }

    public (bool ok, string? error) UpdateRole(string id, NyaRoleInput r)
    {
        var existing = _db.Get<Role>(id);
        if (existing == null || existing.Status == EntityStatus.Deleted) return (false, "角色不存在");
        _db.Execute(
            "UPDATE roles SET display_name=@dn, allowed_task_types=@att, default_capability_ids=@dc, version=version+1 WHERE role_id=@id",
            new
            {
                dn = r.DisplayName,
                att = JsonSerializer.Serialize(r.AllowedTaskTypes ?? Array.Empty<string>()),
                dc = JsonSerializer.Serialize(r.CapabilityIds ?? Array.Empty<string>()),
                id
            });
        return (true, null);
    }

    public (bool ok, string? error) DeleteRole(string id)
    {
        if (BuiltInRoles.Contains(id)) return (false, "內建角色不可刪除");
        var existing = _db.Get<Role>(id);
        if (existing == null || existing.Status == EntityStatus.Deleted) return (false, "角色不存在");
        if (ActiveBindingCount(id) > 0) return (false, "角色仍有有效綁定，請先撤銷");
        _db.Execute("UPDATE roles SET status=@d WHERE role_id=@id", new { d = (int)EntityStatus.Deleted, id });
        _logger.LogInformation("[NyaRoleAdmin] Soft-deleted role {RoleId}", id);
        return (true, null);
    }

    public List<NyaCapabilityView> ListCapabilities()
        => _db.Query<Capability>("SELECT * FROM capabilities", null)
            .Select(c => new NyaCapabilityView(c.CapabilityId, c.Route, c.ResourceType, c.RiskLevelValue, c.ApprovalPolicy))
            .ToList();

    public NyaUserRolesResult GetUserRoles(string userId)
    {
        var principalId = _bindings.Resolve(userId, Channel);
        if (principalId == null) return new NyaUserRolesResult(null, new List<NyaUserRoleView>());
        var roleBindings = _db.Query<RoleBinding>(
            "SELECT * FROM role_bindings WHERE principal_id = @p AND status = @a ORDER BY granted_at DESC",
            new { p = principalId, a = (int)EntityStatus.Active });
        var views = new List<NyaUserRoleView>();
        foreach (var rb in roleBindings)
        {
            var role = _db.Get<Role>(rb.RoleId);
            if (role == null || role.Status == EntityStatus.Deleted) continue;
            views.Add(new NyaUserRoleView(rb.RoleId, role.DisplayName, ParseArr(role.DefaultCapabilityIds).Length, rb.GrantedAt, rb.BindingId));
        }
        return new NyaUserRolesResult(principalId, views);
    }

    public (bool ok, string? error) AssignRole(string userId, string roleId, string by)
    {
        var role = _db.Get<Role>(roleId);
        if (role == null || role.Status == EntityStatus.Deleted) return (false, "角色不存在");

        var principalId = _bindings.Resolve(userId, Channel);
        if (principalId == null)
        {
            principalId = IdGen.New("prn");
            _db.Insert(new Principal { PrincipalId = principalId, ActorType = ActorType.Human, DisplayName = userId, Status = EntityStatus.Active, CreatedAt = DateTime.UtcNow });
            _bindings.Bind(userId, Channel, principalId, by);
        }

        var exists = _db.Query<RoleBinding>(
            "SELECT * FROM role_bindings WHERE principal_id=@p AND role_id=@r AND status=@a",
            new { p = principalId, r = roleId, a = (int)EntityStatus.Active }).Count > 0;
        if (exists) return (true, null); // 冪等

        _db.Insert(new RoleBinding { BindingId = IdGen.New("rb"), PrincipalId = principalId, RoleId = roleId, GrantedBy = by, GrantedAt = DateTime.UtcNow, Status = EntityStatus.Active });
        _logger.LogInformation("[NyaRoleAdmin] Granted role {RoleId} to user {UserId} by {By}", roleId, userId, by);
        return (true, null);
    }

    public bool RevokeRole(string userId, string roleId)
    {
        var principalId = _bindings.Resolve(userId, Channel);
        if (principalId == null) return false;
        var affected = _db.Execute(
            "UPDATE role_bindings SET status=@d WHERE principal_id=@p AND role_id=@r AND status=@a",
            new { d = (int)EntityStatus.Disabled, p = principalId, r = roleId, a = (int)EntityStatus.Active }) > 0;
        if (affected) _logger.LogInformation("[NyaRoleAdmin] Revoked role {RoleId} from user {UserId}", roleId, userId);
        return affected;
    }
}
