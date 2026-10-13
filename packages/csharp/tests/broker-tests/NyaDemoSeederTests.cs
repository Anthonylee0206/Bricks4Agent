using Broker.NyaChat.Abstractions;
using Broker.Services.NyaTools;
using BrokerCore.Data;
using BrokerCore.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

/// <summary>
/// Doc 1 權限 demo 開關（<see cref="NyaDemoSeeder"/>）：把設定的 demo 使用者綁到 role_admin，
/// 讓 <see cref="BrokerToolAuthorizer"/> 對該使用者放行 agent.dispatch（與其他工具）。
/// 用「真的權限授權器」驗證授予鏈是否真的打通閘（不是只檢查列存在）。
/// 使用 %TEMP% 臨時 SQLite（Pooling=False + finally 清理；登記於 CLAUDE.md：nya_demoseed_*）。
/// </summary>
public static class NyaDemoSeederTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0;
        _failed = 0;
        Console.WriteLine("=== NyaChat Demo Seeder Tests (Doc 1 權限 demo 開關) ===");
        Console.WriteLine();

        TestEnabledGrantsAdminAndGateOpens();
        TestDisabledDoesNothing();
        TestIdempotent();

        Console.WriteLine();
        Console.WriteLine($"=== Demo Seeder Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static readonly NyaToolSchema DispatchSchema =
        new() { Name = "dispatch_research_agent", RequiredPermission = "agent.dispatch" };

    private static void TestEnabledGrantsAdminAndGateOpens()
    {
        Console.WriteLine("--- enabled: seed grants role_admin → authorizer opens agent.dispatch ---");
        WithEnv((db, bindings, authorizer) =>
        {
            // 授予前：權限閘擋下（沒有任何綁定）。
            AssertTrue("before-denied", !authorizer.CanUse("u_demo", "line", DispatchSchema));

            NyaDemoSeeder.Seed(db, bindings, Opts(enabled: true, user: "u_demo"), NullLogger.Instance);

            // 授予後：agent.dispatch 放行。
            AssertTrue("after-allowed", authorizer.CanUse("u_demo", "line", DispatchSchema));
            // role_admin = "*"：其他權限也全開（demo 全開的本意）。
            AssertTrue("full-open", authorizer.CanUse("u_demo", "line",
                new NyaToolSchema { Name = "x", RequiredPermission = "quant.read" }));
        });
    }

    private static void TestDisabledDoesNothing()
    {
        Console.WriteLine("--- disabled: no grant, gate stays closed ---");
        WithEnv((db, bindings, authorizer) =>
        {
            NyaDemoSeeder.Seed(db, bindings, Opts(enabled: false, user: "u_demo"), NullLogger.Instance);

            AssertTrue("disabled-denied", !authorizer.CanUse("u_demo", "line", DispatchSchema));
            AssertEqual("no-rolebinding", 0,
                db.Query<RoleBinding>("SELECT * FROM role_bindings WHERE principal_id = @p",
                    new { p = "prn_nyademo" }).Count);
        });
    }

    private static void TestIdempotent()
    {
        Console.WriteLine("--- idempotent: seeding twice → one role_binding + one nya binding, gate open ---");
        WithEnv((db, bindings, authorizer) =>
        {
            NyaDemoSeeder.Seed(db, bindings, Opts(enabled: true, user: "u_demo"), NullLogger.Instance);
            NyaDemoSeeder.Seed(db, bindings, Opts(enabled: true, user: "u_demo"), NullLogger.Instance);

            AssertEqual("one-rolebinding", 1,
                db.Query<RoleBinding>(
                    "SELECT * FROM role_bindings WHERE principal_id = @p AND role_id = 'role_admin' AND status = @a",
                    new { p = "prn_nyademo", a = (int)EntityStatus.Active }).Count);
            AssertEqual("one-nyabinding", 1,
                db.Query<NyaPrincipalBinding>(
                    "SELECT * FROM nya_principal_bindings WHERE user_id = 'u_demo' AND revoked_at IS NULL", null).Count);
            AssertTrue("still-open", authorizer.CanUse("u_demo", "line", DispatchSchema));
        });
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static NyaDemoSeedOptions Opts(bool enabled, string user)
        => new() { Enabled = enabled, UserId = user, Channel = "line", PrincipalId = "prn_nyademo", RoleId = "role_admin" };

    private static void WithEnv(Action<BrokerDb, NyaPrincipalBindingService, BrokerToolAuthorizer> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nya_demoseed_{Guid.NewGuid():N}.db");
        var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        try
        {
            db.EnsureTable<Principal>();
            db.EnsureTable<Role>();
            db.EnsureTable<RoleBinding>();
            // role_admin = 全開（"*"），對齊 BrokerDbInitializer.SeedRoles 的種子。
            db.Insert(new Role
            {
                RoleId = "role_admin",
                DisplayName = "Administrator",
                AllowedTaskTypes = "[\"*\"]",
                DefaultCapabilityIds = "[\"*\"]",
                Status = EntityStatus.Active
            });
            var bindings = new NyaPrincipalBindingService(db, NullLogger<NyaPrincipalBindingService>.Instance);
            var authorizer = new BrokerToolAuthorizer(db, bindings, NullLogger<BrokerToolAuthorizer>.Instance);
            body(db, bindings, authorizer);
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }

    // ── assert helpers ──────────────────────────────────────────────────────────
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
