using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

/// <summary>
/// 寄宿功課 Phase 4-C：既有技術債償還（錯誤目錄 NyaChat-Error-Catalog 列出的四項）。
///   債1 統一 LLM 空回應 reason code —— 散落的 llm_empty / llm_empty_response / "LLM returned empty"
///        收斂成 NyaLlmReasons 單一常量，稽核頁可聚合「thinking 空回應」總量。
///   債2 tools×SupportsTools 啟動期 fail-fast —— chat 任務路由到不支援 tools 的 provider，
///        量化工具掛上後閉環無聲失效 = 金融功能靜默故障，升級成啟動期拒絕。
///   債3 稽核寫入失敗兜底計數器 —— Write 失敗時至少留下可觀測的計數，避免錯誤完全遺失。
///   債4 稽核查詢加 result 維度過濾 —— 管理端一鍵篩 error / skipped。
/// 使用 %TEMP% 臨時 SQLite（Pooling=False + finally 清理）。
/// </summary>
public static class NyaTechDebtTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0;
        _failed = 0;
        Console.WriteLine("=== NyaChat Tech Debt Tests (Phase 4-C 還債) ===");
        Console.WriteLine();

        // 債1 reason code 統一
        TestReasonCodeConstantsExist();
        TestSummarizerUsesUnifiedEmptyReason();

        // 債2 fail-fast
        TestClientFailsFastWhenChatProviderLacksToolSupport();
        TestClientStartsWhenChatProviderSupportsTools();

        // 債3 稽核兜底計數器
        TestAuditWriteFailureIncrementsCounter();
        TestAuditWriteSuccessDoesNotIncrementCounter();

        // 債4 result 維度過濾
        TestGetLogsFiltersByResult();
        TestGetLogsWithoutResultReturnsAll();

        Console.WriteLine();
        Console.WriteLine($"=== Tech Debt Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    // ── 債1 ──────────────────────────────────────────────────────────────────────

    private static void TestReasonCodeConstantsExist()
    {
        Console.WriteLine("--- unified empty-response reason code constant exists ---");
        AssertEqual("empty-const", "llm_empty", NyaLlmReasons.LlmEmpty);
    }

    private static void TestSummarizerUsesUnifiedEmptyReason()
    {
        Console.WriteLine("--- summarizer + extractor route empty responses through the same code ---");
        // 收斂後三處皆引用 NyaLlmReasons.LlmEmpty；此處鎖定常量值不漂移。
        AssertTrue("non-empty", !string.IsNullOrEmpty(NyaLlmReasons.LlmEmpty));
        AssertEqual("stable-value", "llm_empty", NyaLlmReasons.LlmEmpty);
    }

    // ── 債2 ──────────────────────────────────────────────────────────────────────

    private static void TestClientFailsFastWhenChatProviderLacksToolSupport()
    {
        Console.WriteLine("--- ctor throws when chat task routes to a no-tools provider (金融功能靜默故障防護) ---");
        var threw = false;
        try { WithClient(new FakeProvider("noTools", supportsTools: false), "noTools", _ => { }); }
        catch (InvalidOperationException ex)
        {
            threw = true;
            AssertTrue("msg-mentions-tools", ex.Message.Contains("tool", StringComparison.OrdinalIgnoreCase));
        }
        AssertTrue("fail-fast-threw", threw);
    }

    private static void TestClientStartsWhenChatProviderSupportsTools()
    {
        Console.WriteLine("--- ctor succeeds when chat task routes to a tools-capable provider ---");
        var ok = true;
        try { WithClient(new FakeProvider("good", supportsTools: true), "good", _ => { }); }
        catch (Exception ex)
        {
            ok = false;
            Console.Error.WriteLine($"  unexpected throw: {ex.Message}");
        }
        AssertTrue("starts-ok", ok);
    }

    // ── 債3 ──────────────────────────────────────────────────────────────────────

    private static void TestAuditWriteFailureIncrementsCounter()
    {
        Console.WriteLine("--- audit write failure bumps the fallback counter (errors not lost silently) ---");
        WithDb((db, store) =>
        {
            var audit = new NyaAuditLogger(db, store, NullLogger<NyaAuditLogger>.Instance);
            // 丟掉底層表 → 下一次 Insert 必失敗，Write 的 catch 應 bump 計數器。
            db.Execute("DROP TABLE nya_audit_log", null);
            var before = audit.WriteFailureCount;
            audit.LogError("u_x", "chat", "boom");
            AssertTrue("counter-incremented", audit.WriteFailureCount == before + 1);
        });
    }

    private static void TestAuditWriteSuccessDoesNotIncrementCounter()
    {
        Console.WriteLine("--- successful audit write leaves the counter untouched ---");
        WithDb((db, store) =>
        {
            var audit = new NyaAuditLogger(db, store, NullLogger<NyaAuditLogger>.Instance);
            var before = audit.WriteFailureCount;
            audit.LogError("u_y", "chat", "normal");
            AssertEqual("counter-unchanged", before, audit.WriteFailureCount);
        });
    }

    // ── 債4 ──────────────────────────────────────────────────────────────────────

    private static void TestGetLogsFiltersByResult()
    {
        Console.WriteLine("--- GetLogs(result:) returns only matching result rows ---");
        WithDb((db, store) =>
        {
            var audit = new NyaAuditLogger(db, store, NullLogger<NyaAuditLogger>.Instance);
            audit.LogChatOutput("u_r", "hi", "m", 1, 1);          // result=success
            audit.LogError("u_r", "chat", "broke");               // result=error
            audit.LogSummarySkipped("u_r", "t1", "llm_empty");    // result=skipped

            var (errors, errTotal) = audit.GetLogs("u_r", result: "error");
            AssertEqual("only-errors-total", 1, errTotal);
            AssertTrue("only-errors-rows", errors.All(l => l.Result == "error"));

            var (skipped, skTotal) = audit.GetLogs("u_r", result: "skipped");
            AssertEqual("only-skipped-total", 1, skTotal);
            AssertTrue("only-skipped-rows", skipped.All(l => l.Result == "skipped"));
        });
    }

    private static void TestGetLogsWithoutResultReturnsAll()
    {
        Console.WriteLine("--- GetLogs without result filter is unchanged (regression) ---");
        WithDb((db, store) =>
        {
            var audit = new NyaAuditLogger(db, store, NullLogger<NyaAuditLogger>.Instance);
            audit.LogChatOutput("u_a", "hi", "m", 1, 1);
            audit.LogError("u_a", "chat", "broke");

            var (all, total) = audit.GetLogs("u_a");
            AssertEqual("all-total", 2, total);
            AssertEqual("all-rows", 2, all.Count);
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────

    private static void WithClient(INyaLlmProvider provider, string chatProvider, Action<NyaLlmClient> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nya_techdebtfacade_{Guid.NewGuid():N}.db");
        var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        try
        {
            var store = new NyaLlmProfileStore(db, NullLogger<NyaLlmProfileStore>.Instance);
            store.UpsertProfile(NyaLlmProfileEntry.FromProfile("chatp", "x",
                new NyaLlmProfile { Provider = chatProvider, Model = "m" }));
            store.SetRoute(NyaLlmProfileStore.DefaultCallSite, "chatp");
            store.SetRoute("chat", "chatp");
            body(new NyaLlmClient(store, new[] { provider }, NullLogger<NyaLlmClient>.Instance));
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }

    private static void WithDb(Action<BrokerDb, INyaConfigStore<NyaChatConfig>> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nya_techdebt_{Guid.NewGuid():N}.db");
        var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        try
        {
            var store = new FixedConfigStore(new NyaChatConfig());
            body(db, store);
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }

    private sealed class FakeProvider : INyaLlmProvider
    {
        public string Key { get; }
        public bool SupportsTools { get; }
        public FakeProvider(string key, bool supportsTools) { Key = key; SupportsTools = supportsTools; }
        public Task<NyaLlmResponse?> SendAsync(NyaLlmProviderRequest request, CancellationToken ct)
            => Task.FromResult<NyaLlmResponse?>(new NyaLlmResponse { Content = "" });
    }

    private sealed class FixedConfigStore : INyaConfigStore<NyaChatConfig>
    {
        private readonly NyaChatConfig _c;
        public FixedConfigStore(NyaChatConfig c) { _c = c; }
        public NyaChatConfig Current => _c;
        public Task UpdateAsync(NyaChatConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
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
