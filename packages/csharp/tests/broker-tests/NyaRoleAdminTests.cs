using Broker.Services.NyaTools;
using BrokerCore;
using BrokerCore.Data;
using BrokerCore.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

/// <summary>
/// 身分組治理 UI 後端（items 1–2）：角色 CRUD、capability 列舉、使用者↔角色綁定。
/// 操作 broker 全域 roles/role_bindings/principals/nya_principal_bindings。
/// 暫存 SQLite（Pooling=False + finally 清理；登記於 CLAUDE.md：nya_roleadmin_*）。
/// </summary>
public static class NyaRoleAdminTests
{
    private static int _passed, _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0; _failed = 0;
        Console.WriteLine("=== NyaChat Role Admin Tests (身分組治理) ===\n");
        TestListRolesShowsBuiltInAndCaps();
        TestCreateUpdateDeleteRole();
        TestDeleteGuards();
        TestAssignCreatesPrincipalIdempotentAndRevoke();
        TestReviveSoftDeletedRole();
        Console.WriteLine($"\n=== Role Admin Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static void TestListRolesShowsBuiltInAndCaps()
    {
        Console.WriteLine("--- list roles: built-in flag + capability count ---");
        WithSvc((db, svc) =>
        {
            var roles = svc.ListRoles();
            var admin = roles.FirstOrDefault(r => r.RoleId == "role_admin");
            AssertTrue("has-admin", admin != null);
            AssertTrue("admin-builtin", admin!.BuiltIn);
            AssertTrue("admin-all-caps", admin.AllCapabilities);
            var reader = roles.FirstOrDefault(r => r.RoleId == "role_reader");
            AssertTrue("reader-cap-count", reader != null && reader.CapabilityCount == 2 && !reader.AllCapabilities);
        });
    }

    private static void TestCreateUpdateDeleteRole()
    {
        Console.WriteLine("--- create / update / delete custom role ---");
        WithSvc((db, svc) =>
        {
            var (ok, err) = svc.CreateRole(new NyaRoleInput("role_custom", "自訂", new[] { "query" }, new[] { "file.read" }));
            AssertTrue("create-ok", ok && err == null);
            var d = svc.GetRole("role_custom");
            AssertTrue("get-detail", d != null && d!.DisplayName == "自訂" && d.CapabilityIds.Length == 1 && !d.BuiltIn);

            var (uok, _) = svc.UpdateRole("role_custom", new NyaRoleInput("role_custom", "自訂2", new[] { "query", "analysis" }, new[] { "file.read", "file.list" }));
            AssertTrue("update-ok", uok);
            var d2 = svc.GetRole("role_custom");
            AssertTrue("update-applied", d2!.DisplayName == "自訂2" && d2.CapabilityIds.Length == 2 && d2.Version == 2);

            var (dok, _) = svc.DeleteRole("role_custom");
            AssertTrue("delete-ok", dok);
            AssertTrue("gone-from-list", svc.ListRoles().All(r => r.RoleId != "role_custom"));
        });
    }

    private static void TestDeleteGuards()
    {
        Console.WriteLine("--- delete guards: built-in + active bindings ---");
        WithSvc((db, svc) =>
        {
            var (bok, berr) = svc.DeleteRole("role_admin");
            AssertTrue("builtin-blocked", !bok && berr != null && berr.Contains("內建"));

            svc.CreateRole(new NyaRoleInput("role_temp", "暫", Array.Empty<string>(), Array.Empty<string>()));
            svc.AssignRole("u_x", "role_temp", "tester");
            var (gok, gerr) = svc.DeleteRole("role_temp");
            AssertTrue("bound-blocked", !gok && gerr != null && gerr.Contains("綁定"));
        });
    }

    private static void TestAssignCreatesPrincipalIdempotentAndRevoke()
    {
        Console.WriteLine("--- assign (lazy principal + idempotent) then revoke ---");
        WithSvc((db, svc) =>
        {
            var before = svc.GetUserRoles("u_a");
            AssertTrue("no-principal-initially", before.PrincipalId == null && before.Roles.Count == 0);

            var (ok, _) = svc.AssignRole("u_a", "role_reader", "tester");
            AssertTrue("assign-ok", ok);
            var after = svc.GetUserRoles("u_a");
            AssertTrue("principal-created", after.PrincipalId != null);
            AssertEqual("one-role", 1, after.Roles.Count);
            AssertTrue("role-is-reader", after.Roles[0].RoleId == "role_reader");

            // 冪等：再指派同角色 → 仍一筆。
            svc.AssignRole("u_a", "role_reader", "tester");
            AssertEqual("still-one", 1, svc.GetUserRoles("u_a").Roles.Count);

            AssertTrue("revoke-ok", svc.RevokeRole("u_a", "role_reader"));
            AssertEqual("zero-after-revoke", 0, svc.GetUserRoles("u_a").Roles.Count);
        });
    }

    private static void TestReviveSoftDeletedRole()
    {
        Console.WriteLine("--- revive soft-deleted role: create → delete → re-create ---");
        WithSvc((db, svc) =>
        {
            // 建立 role_revive，刪除它
            svc.CreateRole(new NyaRoleInput("role_revive", "初版", new[] { "query" }, new[] { "file.read" }));
            svc.DeleteRole("role_revive");
            AssertTrue("gone-after-delete", svc.ListRoles().All(r => r.RoleId != "role_revive"));
            AssertTrue("get-returns-null", svc.GetRole("role_revive") == null);

            // 重新建立（應復活，不報 role_id 已存在）
            var (ok, err) = svc.CreateRole(new NyaRoleInput("role_revive", "新版", new[] { "query", "analysis" }, Array.Empty<string>()));
            AssertTrue("revive-ok", ok && err == null);

            // 檢查清單中重新出現
            var listed = svc.ListRoles().FirstOrDefault(r => r.RoleId == "role_revive");
            AssertTrue("reappears-in-list", listed != null);

            // GetRole 顯示新 display_name，version == 1
            var detail = svc.GetRole("role_revive");
            AssertTrue("revived-display-name", detail != null && detail!.DisplayName == "新版");
            AssertEqual("revived-version-is-1", 1, detail!.Version);

            // 對一個 active 角色再建立 → 仍應回傳 (false, ...)
            var (dupOk, dupErr) = svc.CreateRole(new NyaRoleInput("role_revive", "衝突", Array.Empty<string>(), Array.Empty<string>()));
            AssertTrue("live-dup-blocked", !dupOk && dupErr != null);
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static void WithSvc(Action<BrokerDb, NyaRoleAdminService> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nya_roleadmin_{Guid.NewGuid():N}.db");
        var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        try
        {
            db.EnsureTable<Role>(); db.EnsureTable<Capability>();
            db.EnsureTable<Principal>(); db.EnsureTable<RoleBinding>();
            db.EnsureTable<NyaPrincipalBinding>();
            db.Insert(new Role { RoleId = "role_admin", DisplayName = "Administrator", AllowedTaskTypes = "[\"*\"]", DefaultCapabilityIds = "[\"*\"]", Status = EntityStatus.Active });
            db.Insert(new Role { RoleId = "role_reader", DisplayName = "Reader", AllowedTaskTypes = "[\"query\"]", DefaultCapabilityIds = "[\"file.read\",\"file.list\"]", Status = EntityStatus.Active });
            db.Insert(new Capability { CapabilityId = "file.read", Route = "file/read", ResourceType = "file", ApprovalPolicy = "auto" });
            db.Insert(new Capability { CapabilityId = "file.list", Route = "file/list", ResourceType = "file", ApprovalPolicy = "auto" });
            var bindings = new NyaPrincipalBindingService(db, NullLogger<NyaPrincipalBindingService>.Instance);
            var svc = new NyaRoleAdminService(db, bindings, NullLogger<NyaRoleAdminService>.Instance);
            body(db, svc);
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
    }

    private static void AssertEqual<T>(string name, T expected, T actual)
    {
        if (Equals(expected, actual)) { Console.WriteLine($"  [PASS] {name}"); _passed++; }
        else { Console.Error.WriteLine($"  [FAIL] {name}: expected '{expected}', got '{actual}'"); _failed++; }
    }
    private static void AssertTrue(string name, bool cond)
    {
        if (cond) { Console.WriteLine($"  [PASS] {name}"); _passed++; }
        else { Console.Error.WriteLine($"  [FAIL] {name}: expected true"); _failed++; }
    }
}
