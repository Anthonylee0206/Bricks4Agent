using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

/// <summary>Doc 3 Plan C：LLM 管理面（param catalog / 模板 / api_key 保護 / 遮罩）。</summary>
public static class NyaLlmAdminTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0; _failed = 0;
        Console.WriteLine("=== NyaChat LLM Admin Tests (Doc 3 Plan C) ===");
        Console.WriteLine();

        TestParamCatalogHasCoreParams();
        TestProviderTemplates();
        TestSecretRoundTripAndMask();
        TestStoreEncryptsApiKeyAtRest();
        TestLlmChangesWriteWebVisibleAudit();

        Console.WriteLine();
        Console.WriteLine($"=== LLM Admin Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static void TestParamCatalogHasCoreParams()
    {
        Console.WriteLine("--- param catalog: core params with bounds + doc + common flag ---");
        var cat = NyaLlmParamCatalog.All;
        var temp = cat.FirstOrDefault(p => p.Key == "temperature");
        AssertTrue("has-temperature", temp != null);
        AssertEqual("temp-min", 0d, temp!.Min);
        AssertEqual("temp-max", 2d, temp.Max);
        AssertTrue("temp-common", temp.Common);
        AssertTrue("temp-doc", !string.IsNullOrWhiteSpace(temp.Doc));
        AssertTrue("has-top_p", cat.Any(p => p.Key == "top_p"));
        AssertTrue("has-max_context_tokens", cat.Any(p => p.Key == "max_context_tokens"));
        AssertTrue("has-timeout_seconds", cat.Any(p => p.Key == "timeout_seconds"));
    }

    private static void TestProviderTemplates()
    {
        Console.WriteLine("--- provider templates: four families map to provider + defaults ---");
        var t = NyaLlmProviderTemplates.All;
        AssertEqual("template-count", 4, t.Count);
        var chatgpt = t.First(x => x.Id == "chatgpt");
        AssertEqual("chatgpt-provider", "openai_chat", chatgpt.Provider);
        AssertEqual("claude-provider", "anthropic", t.First(x => x.Id == "claude").Provider);
        AssertEqual("gemini-provider", "gemini", t.First(x => x.Id == "gemini").Provider);
        AssertEqual("local-provider", "ollama", t.First(x => x.Id == "local").Provider);
        AssertTrue("chatgpt-has-defaults", chatgpt.DefaultParams.ContainsKey("temperature"));
    }

    private static void TestSecretRoundTripAndMask()
    {
        Console.WriteLine("--- secret: protector round-trips; mask hides middle ---");
        var prot = new ReversibleTestProtector();
        AssertEqual("unprotect-roundtrip", "sk-secret", prot.Unprotect(prot.Protect("sk-secret")));
        AssertEqual("mask-short", "****", NyaSecretMask.Mask("abc"));
        var masked = NyaSecretMask.Mask("sk-abcdefgh");
        AssertTrue("mask-keeps-tail", masked.EndsWith("efgh"));
        AssertTrue("mask-hides", !masked.Contains("abcd"));
    }

    private static void TestStoreEncryptsApiKeyAtRest()
    {
        Console.WriteLine("--- store: api_key stored encrypted, GetProfile/ResolveProfile return plaintext ---");
        var path = Path.Combine(Path.GetTempPath(), $"nya_llmadmin_{Guid.NewGuid():N}.db");
        var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        try
        {
            var store = new NyaLlmProfileStore(db, NullLogger<NyaLlmProfileStore>.Instance, new ReversibleTestProtector());
            store.UpsertProfile(NyaLlmProfileEntry.FromProfile("p1", "claude",
                new NyaLlmProfile { Provider = "anthropic", Model = "m", ApiKey = "sk-plain" }));
            store.SetRoute(NyaLlmProfileStore.DefaultCallSite, "p1");

            // 1) raw DB column is encrypted (not plaintext)
            var rawKey = db.Scalar<string>("SELECT api_key FROM nya_llm_profiles WHERE profile_id = 'p1'", null);
            AssertTrue("at-rest-encrypted", rawKey != null && rawKey != "sk-plain" && rawKey.Contains("sk-plain") == false || rawKey == "ENC(sk-plain)");
            AssertEqual("at-rest-exact", "ENC(sk-plain)", rawKey);

            // 2) GetProfile returns plaintext (decrypted)
            AssertEqual("get-decrypted", "sk-plain", store.GetProfile("p1")!.ApiKey);
            // 3) ResolveProfile returns plaintext (decrypted) for use by NyaLlmClient
            AssertEqual("resolve-decrypted", "sk-plain", store.ResolveProfile(NyaLlmProfileStore.DefaultCallSite).ApiKey);
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }

    // #4：用網頁改 LLM 設定後要有可在管理面板看到的稽核紀錄（全域 __sys__ 動作），
    // 比照既有 tool_toggle / soul_change / config_update。之前 profile/route 異動完全靜默。
    private static void TestLlmChangesWriteWebVisibleAudit()
    {
        Console.WriteLine("--- audit: llm profile/route changes land in web-visible (__sys__) audit log ---");
        var path = Path.Combine(Path.GetTempPath(), $"nya_llmadmin_{Guid.NewGuid():N}.db");
        var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        try
        {
            var audit = new NyaAuditLogger(db, new FixedConfigStore(new NyaChatConfig()), NullLogger<NyaAuditLogger>.Instance);

            audit.LogLlmProfileUpsert("chat_default", "openai_chat", "qwen3.6-35b-a3b-Q4");
            audit.LogLlmRouteSet("chat", "chat_default");
            audit.LogLlmProfileDelete("stale_profile");

            // 管理面板查全域日誌（userId=null）；這三筆都該帶 __sys__ user 與對應 action。
            var (logs, total) = audit.GetLogs(userId: null, limit: 50);
            AssertTrue("audit-has-three", total >= 3);

            var upsert = logs.FirstOrDefault(l => l.Action == "llm_profile_upsert");
            AssertTrue("upsert-logged", upsert != null);
            AssertEqual("upsert-sys-user", "__sys__", upsert!.UserId);
            AssertTrue("upsert-detail-has-profile", upsert.DetailJson.Contains("chat_default"));
            AssertTrue("upsert-detail-has-model", upsert.DetailJson.Contains("qwen3.6-35b-a3b-Q4"));

            var route = logs.FirstOrDefault(l => l.Action == "llm_route_set");
            AssertTrue("route-logged", route != null);
            AssertTrue("route-detail-has-callsite", route!.DetailJson.Contains("chat"));

            var del = logs.FirstOrDefault(l => l.Action == "llm_profile_delete");
            AssertTrue("delete-logged", del != null);
            AssertTrue("delete-detail-has-profile", del!.DetailJson.Contains("stale_profile"));

            // 稽核不得記下 api_key（即使未來方法簽章變動，也不該洩漏祕密）。
            AssertTrue("no-secret-in-audit", !upsert.DetailJson.Contains("api_key"));
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }

    private sealed class FixedConfigStore : INyaConfigStore<NyaChatConfig>
    {
        private readonly NyaChatConfig _c;
        public FixedConfigStore(NyaChatConfig c) { _c = c; }
        public NyaChatConfig Current => _c;
        public Task UpdateAsync(NyaChatConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class ReversibleTestProtector : INyaSecretProtector
    {
        public string Protect(string plain) => string.IsNullOrEmpty(plain) ? plain : "ENC(" + plain + ")";
        public string Unprotect(string stored) =>
            stored != null && stored.StartsWith("ENC(") && stored.EndsWith(")") ? stored[4..^1] : stored ?? "";
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
