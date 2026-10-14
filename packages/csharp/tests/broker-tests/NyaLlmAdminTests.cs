using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

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
        TestHotConfigAndEffectiveSampling();

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
        AssertEqual("template-count", 6, t.Count);
        var chatgpt = t.First(x => x.Id == "chatgpt");
        AssertEqual("chatgpt-provider", "openai_chat", chatgpt.Provider);
        AssertEqual("claude-provider", "anthropic", t.First(x => x.Id == "claude").Provider);
        AssertEqual("gemini-provider", "gemini", t.First(x => x.Id == "gemini").Provider);
        AssertEqual("local-provider", "ollama", t.First(x => x.Id == "local").Provider);
        AssertTrue("chatgpt-has-defaults", chatgpt.DefaultParams.ContainsKey("temperature"));
        AssertEqual("lmstudio-provider", "openai_chat", t.First(x => x.Id == "lmstudio").Provider);
        foreach (var template in t)
            AssertTrue("template-full-endpoint-" + template.Id, new Uri(template.DefaultBaseUrl).AbsolutePath.Trim('/').Length > 0);
    }

    private static void TestHotConfigAndEffectiveSampling()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nya_hot_config_{Guid.NewGuid():N}.db");
        try
        {
            using var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
            var config = new NyaChatConfigStore(new NyaChatConfig { AuditEnabled = false }, db, NullLogger<NyaChatConfigStore>.Instance);
            using var patch = System.Text.Json.JsonDocument.Parse("{\"ShortTermMessageLimit\":1000,\"DefaultMaxContextTokens\":131072}");
            config.PatchAsync(patch.RootElement, "console-admin", "super_admin", default).GetAwaiter().GetResult();
            AssertEqual("hot-message-limit", 1000, config.Current.ShortTermMessageLimit);
            AssertEqual("hot-context-budget", 131072, config.Current.DefaultMaxContextTokens);
            AssertEqual("hot-preserves-other-fields", 2048, config.Current.MaxOutputTokens);
            AssertTrue("hot-audit-even-disabled", db.GetAll<NyaAuditLog>().Any(l => l.Action == "config_update" && l.DetailJson.Contains("console-admin")));
            var reloaded = new NyaChatConfigStore(new NyaChatConfig { MaxToolRounds = 5 }, db, NullLogger<NyaChatConfigStore>.Instance);
            AssertEqual("hot-reload-overrides", 1000, reloaded.Current.ShortTermMessageLimit);
            AssertEqual("hot-reload-unoverridden-default", 5, reloaded.Current.MaxToolRounds);
            config.ResetAsync("console-admin", "super_admin", default).GetAwaiter().GetResult();
            AssertEqual("hot-reset-to-seed", 200, config.Current.ShortTermMessageLimit);
            AssertTrue("hot-reset-drops-row", db.Get<NyaConfigEntry>("nya:chat") == null);
            var entry = new NyaLlmProfileEntry
            {
                ProfileId = "sampling", Provider = "openai_chat", Model = "model",
                ParamsJson = "{\"temperature\":0.8,\"presence_penalty\":0.4,\"model_overrides\":{\"temperature\":0.2,\"presence_penalty\":1.5,\"top_k\":20}}"
            };
            var profile = entry.ToProfile();
            var sampling = NyaLlmAdminSettings.Sampling(profile, new NyaLlmRequest { Temperature = profile.Temperature, TopP = profile.TopP });
            AssertEqual("effective-overrides-temperature", 0.2, sampling["temperature"]!.GetValue<double>());
            AssertEqual("effective-overrides-penalty", 1.5, sampling["presence_penalty"]!.GetValue<double>());
            AssertEqual("effective-freeform-param", 20d, sampling["top_k"]!.GetValue<double>());
            AssertEqual("real-call-site-count", 3, NyaLlmAdminSettings.CallSites.Count);
            AssertTrue("no-tool-call-sites", NyaLlmAdminSettings.CallSites.All(s => !s.StartsWith("tool:")));
        }
        finally
        {
            foreach (var file in new[] { path, path + "-shm", path + "-wal" })
                if (File.Exists(file)) File.Delete(file);
        }
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

            var rawKey = db.Scalar<string>("SELECT api_key FROM nya_llm_profiles WHERE profile_id = 'p1'", null);
            AssertTrue("at-rest-encrypted", rawKey != null && rawKey != "sk-plain" && rawKey.Contains("sk-plain") == false || rawKey == "ENC(sk-plain)");
            AssertEqual("at-rest-exact", "ENC(sk-plain)", rawKey);

            AssertEqual("get-decrypted", "sk-plain", store.GetProfile("p1")!.ApiKey);

            AssertEqual("resolve-decrypted", "sk-plain", store.ResolveProfile(NyaLlmProfileStore.DefaultCallSite).ApiKey);
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch {  }
        }
    }

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

            AssertTrue("no-secret-in-audit", !upsert.DetailJson.Contains("api_key"));
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch {  }
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
