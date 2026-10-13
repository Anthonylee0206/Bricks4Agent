using Broker.NyaChat;
using BrokerCore.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

/// <summary>
/// 資料隔離原則（2026-06-13）：「broker 原則上不過 AI 產出」——
/// NyaChat 的所有持久化（訊息/事實/摘要/話題/稽核/配置）必須寫入自己的 SQLite（nyachat.db），
/// broker.db 不得出現任何 nya_* 表。治理資料（nya_principal_bindings）屬授權非 AI 產出，留在 broker.db。
/// 驗證方式：以 DI 容器組裝（與 broker 啟動同路徑），廣播 broker BrokerDb 與 NyaChat DbPath 指向
/// 不同 temp 檔，寫入後直接驗兩個檔案的 sqlite_master 與資料列。
/// 使用 %TEMP% 臨時 SQLite（Pooling=False + finally 清理，遵循 CLAUDE.md 規約）。
/// </summary>
public static class NyaDbSeparationTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0;
        _failed = 0;
        Console.WriteLine("=== NyaChat DB Separation Tests (broker 不過 AI 產出) ===");
        Console.WriteLine();

        TestNyaWritesGoToNyaDbOnly();
        TestGovernanceBindingStaysInBrokerDb();

        Console.WriteLine();
        Console.WriteLine($"=== DB Separation Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static void TestNyaWritesGoToNyaDbOnly()
    {
        Console.WriteLine("--- nya messages/audit/config land in nyachat.db; broker.db has zero nya_* tables ---");
        var brokerPath = TempPath("nya_dbsep_broker");
        var nyaPath = TempPath("nya_dbsep_nya");
        BrokerDb? brokerDb = null;
        try
        {
            brokerDb = BrokerDb.UseSqlite($"Data Source={brokerPath};Pooling=False");

            var services = new ServiceCollection();
            services.AddSingleton(brokerDb);                                    // 模擬 broker 宿主的共用 DB
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));     // 免拉整套 Logging
            // DbPath 附加 Pooling=False：NyaDb Dispose 後連線池不得鎖住檔案，finally 才刪得掉（CLAUDE.md 清理規約）。
            services.AddNyaChat(new NyaChatConfig { Enabled = true, DbPath = $"{nyaPath};Pooling=False" });

            using (var sp = services.BuildServiceProvider())
            {
                var mem = sp.GetRequiredService<NyaMemoryService>();
                var audit = sp.GetRequiredService<NyaAuditLogger>();

                var topicId = mem.GetOrCreateActiveTopic("u_sep");
                mem.AppendMessage("u_sep", "user", "hello separation", topicId);
                audit.LogChatOutput("u_sep", "reply", "test-model", 5, 1);

                // broker.db：不得有任何 nya_* 表（治理綁定表除外——它由 broker 側服務建立，本容器未註冊）
                var brokerNyaTables = brokerDb.Scalar<int>(
                    "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'nya_%'", null);
                AssertEqual("broker-no-nya-tables", 0, brokerNyaTables);
            }

            // nyachat.db：訊息與稽核都落在這裡（容器已 Dispose，另開連線驗檔案內容）
            using var verify = BrokerDb.UseSqlite($"Data Source={nyaPath};Pooling=False");
            AssertEqual("nya-message-row", 1,
                verify.Scalar<int>("SELECT COUNT(*) FROM nya_messages WHERE user_id = 'u_sep'", null));
            AssertEqual("nya-audit-row", 1,
                verify.Scalar<int>("SELECT COUNT(*) FROM nya_audit_log WHERE user_id = 'u_sep'", null));
            AssertTrue("nya-has-tables",
                verify.Scalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'nya_%'", null) >= 3);
        }
        finally
        {
            brokerDb?.Dispose();
            Cleanup(brokerPath, nyaPath);
        }
    }

    private static void TestGovernanceBindingStaysInBrokerDb()
    {
        Console.WriteLine("--- governance binding (nya_principal_bindings) stays in broker.db by design ---");
        var brokerPath = TempPath("nya_dbsep_gov");
        try
        {
            using var brokerDb = BrokerDb.UseSqlite($"Data Source={brokerPath};Pooling=False");
            var bindings = new Broker.Services.NyaTools.NyaPrincipalBindingService(
                brokerDb, NullLogger<Broker.Services.NyaTools.NyaPrincipalBindingService>.Instance);
            bindings.Bind("u_gov", "line", "prn_gov", "test");

            AssertEqual("binding-in-broker-db", 1,
                brokerDb.Scalar<int>("SELECT COUNT(*) FROM nya_principal_bindings WHERE user_id = 'u_gov'", null));
        }
        finally
        {
            Cleanup(brokerPath);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static string TempPath(string prefix)
        => Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}.db");

    private static void Cleanup(params string[] paths)
    {
        foreach (var basePath in paths)
            foreach (var p in new[] { basePath, basePath + "-shm", basePath + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
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
