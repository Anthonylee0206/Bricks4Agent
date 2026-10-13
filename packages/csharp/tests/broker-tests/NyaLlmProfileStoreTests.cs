using Broker.NyaChat;
using BrokerCore.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

/// <summary>
/// Doc 3 Plan A：LLM profile/route 專店（CRUD / 解析+fallback / 映射 / 遷移）。
/// 使用 %TEMP% 臨時 SQLite（Pooling=False + finally 清理，遵循 CLAUDE.md 規約）。
/// </summary>
public static class NyaLlmProfileStoreTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0; _failed = 0;
        Console.WriteLine("=== NyaChat LLM Profile Store Tests (Doc 3 Plan A) ===");
        Console.WriteLine();

        TestEntryToProfileRoundTrip();
        TestProfileCrud();
        TestRouteResolveFallback();
        TestResolveThrowsWhenNoDefault();
        TestClearRouteAndDeleteGuards();
        TestMigrateFromLegacy();

        Console.WriteLine();
        Console.WriteLine($"=== LLM Profile Store Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static void TestEntryToProfileRoundTrip()
    {
        Console.WriteLine("--- entry params_json <-> NyaLlmProfile round-trips typed fields ---");
        var profile = new NyaLlmProfile
        {
            Provider = "openai_chat", BaseUrl = "http://x", ApiKey = "k", Model = "m",
            Temperature = 0.3f, TopP = 0.8f, TimeoutSeconds = 90, MaxContextTokens = 16000,
            ModelOverrides = new() { ["top_k"] = 20 }, EnableThinking = false, ThinkingBudget = 256
        };
        var entry = NyaLlmProfileEntry.FromProfile("p1", "claude", profile);
        AssertEqual("entry-id", "p1", entry.ProfileId);
        AssertEqual("entry-provider", "openai_chat", entry.Provider);
        AssertEqual("entry-template", "claude", entry.Template);

        var back = entry.ToProfile();
        AssertEqual("rt-provider", "openai_chat", back.Provider);
        AssertEqual("rt-base", "http://x", back.BaseUrl);
        AssertEqual("rt-model", "m", back.Model);
        AssertEqual("rt-temp", 0.3f, back.Temperature);
        AssertEqual("rt-topp", 0.8f, back.TopP);
        AssertEqual("rt-timeout", 90, back.TimeoutSeconds);
        AssertEqual("rt-ctx", 16000, back.MaxContextTokens);
        AssertEqual("rt-thinking", false, back.EnableThinking);
        AssertEqual("rt-budget", 256, back.ThinkingBudget);
        AssertTrue("rt-overrides", back.ModelOverrides != null && back.ModelOverrides["top_k"] == 20);
    }

    private static void TestProfileCrud()
    {
        Console.WriteLine("--- profile CRUD: upsert / get / list / delete ---");
        WithStore(store =>
        {
            store.UpsertProfile(NyaLlmProfileEntry.FromProfile("p1", "local",
                new NyaLlmProfile { Provider = "ollama", Model = "qwen" }));
            AssertEqual("get-model", "qwen", store.GetProfile("p1")!.Model);
            AssertEqual("list-one", 1, store.ListProfiles().Count);

            store.UpsertProfile(NyaLlmProfileEntry.FromProfile("p1", "local",
                new NyaLlmProfile { Provider = "ollama", Model = "qwen2" }));
            AssertEqual("update-model", "qwen2", store.GetProfile("p1")!.Model);
            AssertEqual("still-one", 1, store.ListProfiles().Count);

            AssertTrue("delete-ok", store.DeleteProfile("p1"));
            AssertTrue("gone", store.GetProfile("p1") == null);
        });
    }

    private static void TestRouteResolveFallback()
    {
        Console.WriteLine("--- resolve: call_site -> route -> profile; unmapped falls to __default__ ---");
        WithStore(store =>
        {
            store.UpsertProfile(NyaLlmProfileEntry.FromProfile("def", "local",
                new NyaLlmProfile { Provider = "ollama", Model = "default-m" }));
            store.UpsertProfile(NyaLlmProfileEntry.FromProfile("chatp", "chatgpt",
                new NyaLlmProfile { Provider = "openai_chat", Model = "chat-m" }));
            store.SetRoute(NyaLlmProfileStore.DefaultCallSite, "def");
            store.SetRoute("chat", "chatp");

            AssertEqual("mapped", "chat-m", store.ResolveProfile("chat").Model);
            AssertEqual("unmapped-falls-default", "default-m", store.ResolveProfile("tool:whatever").Model);
            AssertEqual("route-count", 2, store.ListRoutes().Count);
        });
    }

    private static void TestResolveThrowsWhenNoDefault()
    {
        Console.WriteLine("--- resolve: missing __default__ for unmapped call site throws (mis-seed guard) ---");
        WithStore(store =>
        {
            var threw = false;
            try { store.ResolveProfile("chat"); }
            catch (InvalidOperationException) { threw = true; }
            AssertTrue("throws-no-default", threw);
        });
    }

    private static void TestClearRouteAndDeleteGuards()
    {
        Console.WriteLine("--- 「（預設）」clears route to __default__; dangling route / default-profile delete refused ---");
        WithStore(store =>
        {
            store.UpsertProfile(NyaLlmProfileEntry.FromProfile("def", "local",
                new NyaLlmProfile { Provider = "ollama", Model = "default-m" }));
            store.UpsertProfile(NyaLlmProfileEntry.FromProfile("alt", "local",
                new NyaLlmProfile { Provider = "ollama", Model = "alt-m" }));
            store.SetRoute(NyaLlmProfileStore.DefaultCallSite, "def");
            store.SetRoute("chat", "alt");

            AssertTrue("clear-ok", store.ClearRoute("chat"));
            AssertEqual("cleared-falls-default", "default-m", store.ResolveProfile("chat").Model);
            AssertTrue("clear-default-refused", Throws(() => store.ClearRoute(NyaLlmProfileStore.DefaultCallSite)));

            AssertTrue("dangling-route-refused", Throws(() => store.SetRoute("chat", "ghost")));
            AssertEqual("still-default-after-refuse", "default-m", store.ResolveProfile("chat").Model);

            // 刪除被路由指向的 profile → 路由一併移除、回落預設
            store.SetRoute("summarization", "alt");
            AssertTrue("delete-routed-ok", store.DeleteProfile("alt"));
            AssertEqual("deleted-route-falls-default", "default-m", store.ResolveProfile("summarization").Model);

            AssertTrue("delete-default-refused", Throws(() => store.DeleteProfile("def")));
            AssertTrue("default-kept", store.GetProfile("def") != null);
        });
    }

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch (InvalidOperationException) { return true; }
    }

    private static void TestMigrateFromLegacy()
    {
        Console.WriteLine("--- migrate legacy NyaChatConfig -> store (idempotent), resolution preserved ---");
        WithStore(store =>
        {
            var config = new NyaChatConfig
            {
                ChatProvider = "openai_chat", ChatBaseUrl = "http://chat", ChatApiKey = "ck",
                ChatModel = "chat-model", Temperature = 0.55f, TopP = 0.85f, ChatTimeoutSeconds = 77
            };
            var router = new StaticTaskRouter(NullLogger<StaticTaskRouter>.Instance);

            var migrated1 = store.MigrateFromLegacyIfEmpty(config, router);
            AssertTrue("did-migrate", migrated1);

            // 三個內建呼叫點 + __default__ 都能解析，且與 router 直接解析等效
            AssertEqual("chat-model", "chat-model", store.ResolveProfile("chat").Model);
            AssertEqual("chat-temp", 0.55f, store.ResolveProfile("chat").Temperature);
            AssertEqual("extract-resolves", router.Resolve("fact_extraction", config).Model,
                store.ResolveProfile("fact_extraction").Model);
            AssertEqual("default-is-chat", "chat-model", store.ResolveProfile("unmapped").Model);

            // 冪等：再跑一次不重覆遷移、不增列
            var profilesAfter = store.ListProfiles().Count;
            var migrated2 = store.MigrateFromLegacyIfEmpty(config, router);
            AssertTrue("skip-second", !migrated2);
            AssertEqual("no-extra-profiles", profilesAfter, store.ListProfiles().Count);
        });
    }

    private static void WithStore(Action<NyaLlmProfileStore> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nya_llmstore_{Guid.NewGuid():N}.db");
        var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        try
        {
            var store = new NyaLlmProfileStore(db, NullLogger<NyaLlmProfileStore>.Instance);
            body(store);
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
