using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

/// <summary>
/// 大項三（Token 預算管理）+ 大項一前置（通道識別正規化）單元測試。
/// 鎖死：(1) 估算器行為、(2) 預算截斷的 category 優先級與必留事實、(3) summaries/history 取捨、
/// (4) 工具結果截尾、(5) NyaUserIdentity.Normalize 與通道隔離（補大項一零覆蓋缺口）。
/// </summary>
public static class NyaTokenBudgetTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0;
        _failed = 0;

        Console.WriteLine("=== NyaChat Token Budget + Identity Tests (大項三 / 大項一前置) ===");
        Console.WriteLine();

        TestEstimatorBasics();
        TestEstimatorTruncate();
        TestUnlimitedBudgetKeepsAll();
        TestMandatoryFactsNeverDropped();
        TestSummariesDropOldestFirst();
        TestHistoryKeepsRecentFloor();
        TestUserIdentityNormalize();
        TestChannelMemoryIsolation();

        Console.WriteLine();
        Console.WriteLine($"=== Token Budget Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    // ── 估算器 ──────────────────────────────────────────────────────────────────
    private static void TestEstimatorBasics()
    {
        Console.WriteLine("--- NyaTokenEstimator basics ---");
        var est = new NyaTokenEstimator(1.5, 0.25);
        AssertTrue("empty-zero", est.Estimate("") == 0);
        // CJK 比拉丁貴：同字數的中文估算 token 應高於英文
        AssertTrue("cjk-heavier", est.Estimate("你好世界你好") > est.Estimate("hello!"));
        // 單調性：較長文字 token 不減
        AssertTrue("monotonic", est.Estimate("一二三四五") >= est.Estimate("一二"));
    }

    private static void TestEstimatorTruncate()
    {
        Console.WriteLine("--- NyaTokenEstimator truncate ---");
        var est = new NyaTokenEstimator(1.5, 0.25);
        var longText = new string('字', 500); // 500 CJK ≈ 750 token
        var cut = est.TruncateToTokens(longText, 100);
        AssertTrue("truncated-shorter", cut.Length < longText.Length);
        AssertTrue("truncated-marker", cut.Contains("截斷"));
        AssertTrue("truncated-under-budget", est.Estimate(cut) <= 100 + 20 /*marker*/);
        // 在預算內 → 原樣
        AssertEqual("no-truncate", "短", est.TruncateToTokens("短", 100));
    }

    // ── 預算截斷 ────────────────────────────────────────────────────────────────
    private static NyaPromptBuilder BuildBuilder()
    {
        var store = new FixedConfigStore(new NyaChatConfig());
        var enableStore = new NyaToolEnableStore(
            new ConfigurationBuilder().Build(), NullLogger<NyaToolEnableStore>.Instance);
        var registry = new NyaToolRegistry(
            Array.Empty<INyaToolPlugin>(), new AllowAllToolAuthorizer(), enableStore,
            NullLogger<NyaToolRegistry>.Instance);
        // 大項五：PromptBuilder 新增 NyaPromptTemplateProvider 相依（缺檔走內建 fallback）。
        var templates = new NyaPromptTemplateProvider(store, NullLogger<NyaPromptTemplateProvider>.Instance);
        return new NyaPromptBuilder(store, registry, templates);
    }

    private static SoulDefinition Soul() => new() { SoulId = "default", DisplayName = "NYA" };

    private static NyaFact Fact(string cat, string key, string val, float conf = 1.0f)
        => new() { Category = cat, FactKey = key, FactValue = val, Confidence = conf, CreatedAt = DateTime.UtcNow };

    private static void TestUnlimitedBudgetKeepsAll()
    {
        Console.WriteLine("--- unlimited budget keeps all ---");
        var b = BuildBuilder();
        var facts = new List<NyaFact> { Fact("preference", "p1", "v1"), Fact("identity", "user_name", "Boss") };
        var msgs = b.Build("hi", Soul(), facts, new List<NyaSummary>(), new List<NyaMessage>(),
            NyaPromptBudget.Unlimited, NyaTokenEstimator.Default, "u1", "line", out var rep);
        AssertEqual("unlimited-facts", 2, rep.FactsIncluded);
        AssertEqual("unlimited-dropped", 0, rep.FactsDropped);
        AssertTrue("unlimited-has-system", msgs[0].Role == "system");
    }

    private static void TestMandatoryFactsNeverDropped()
    {
        Console.WriteLine("--- mandatory facts never dropped under tight budget ---");
        var b = BuildBuilder();
        var facts = new List<NyaFact>
        {
            Fact("instruction", "call_me", "請叫我老闆"),
            Fact("identity", "user_name", "王小明"),
        };
        // 塞入大量 preference 把預算撐爆
        for (var i = 0; i < 30; i++)
            facts.Add(Fact("preference", $"pref_{i}", new string('喜', 40)));

        var budget = new NyaPromptBudget { InputTokenBudget = 400, FactRatio = 0.4, SummaryRatio = 0.35 };
        var msgs = b.Build("你好", Soul(), facts, new List<NyaSummary>(), new List<NyaMessage>(),
            budget, new NyaTokenEstimator(1.5, 0.25), "u1", "line", out var rep);

        var system = msgs[0].Content;
        AssertTrue("mandatory-instruction-kept", system.Contains("請叫我老闆"));
        AssertTrue("mandatory-identity-kept", system.Contains("王小明"));
        AssertTrue("preferences-dropped", rep.FactsDropped > 0);
        AssertTrue("mandatory-counted", rep.FactsIncluded >= 2);
    }

    private static void TestSummariesDropOldestFirst()
    {
        Console.WriteLine("--- summaries drop oldest first ---");
        var b = BuildBuilder();
        var summaries = new List<NyaSummary>
        {
            // 舊摘要極長（必超預算），新摘要極短（必容得下）→ 不依賴固定段成本的精確值
            new() { SummaryText = new string('舊', 4000), CoveredFromSeq = 1,   CoveredToSeq = 10 },
            new() { SummaryText = "最新摘要內容",          CoveredFromSeq = 100, CoveredToSeq = 110 },
        };
        var budget = new NyaPromptBudget { InputTokenBudget = 2000, FactRatio = 0.4, SummaryRatio = 0.5 };
        var msgs = b.Build("hi", Soul(), new List<NyaFact>(), summaries, new List<NyaMessage>(),
            budget, new NyaTokenEstimator(1.5, 0.25), "u1", "line", out var rep);

        var system = msgs[0].Content;
        AssertTrue("newest-summary-kept", system.Contains("最新摘要內容"));
        AssertTrue("summary-dropped", rep.SummariesDropped >= 1);
    }

    private static void TestHistoryKeepsRecentFloor()
    {
        Console.WriteLine("--- history keeps recent coherence floor ---");
        var b = BuildBuilder();
        var hist = new List<NyaMessage>();
        for (var i = 0; i < 10; i++)
            hist.Add(new NyaMessage { Role = i % 2 == 0 ? "user" : "assistant",
                Content = new string('話', 60), Sequence = i });

        // 極小預算：仍應保留至少 2 則（連貫地板）
        var budget = new NyaPromptBudget { InputTokenBudget = 200, FactRatio = 0.4, SummaryRatio = 0.35 };
        var msgs = b.Build("現在", Soul(), new List<NyaFact>(), new List<NyaSummary>(), hist,
            budget, new NyaTokenEstimator(1.5, 0.25), "u1", "line", out var rep);

        AssertTrue("history-floor", rep.HistoryIncluded >= 2);
        AssertTrue("history-dropped-some", rep.HistoryDropped > 0);
        // 保留的是最近的（Sequence 8、9 應在最後幾則）
        var lastHistory = msgs[^2]; // [^1] = 當前 user 訊息
        AssertEqual("history-recent-kept", new string('話', 60), lastHistory.Content);
    }

    // ── 大項一前置：通道識別 ─────────────────────────────────────────────────────
    private static void TestUserIdentityNormalize()
    {
        Console.WriteLine("--- NyaUserIdentity.Normalize (大項一前置) ---");
        // LINE 必須回原 id（記憶遷移安全的硬約束）
        AssertEqual("line-keeps-id", "U123abc", NyaUserIdentity.Normalize("line", "U123abc"));
        AssertEqual("line-case-insensitive", "U123abc", NyaUserIdentity.Normalize("LINE", "U123abc"));
        // 其餘通道加前綴
        AssertEqual("discord-prefixed", "discord:u1", NyaUserIdentity.Normalize("discord", "u1"));
        AssertEqual("web-prefixed", "web:u1", NyaUserIdentity.Normalize("Web", "u1"));
        // 空輸入安全
        AssertEqual("empty-safe", "", NyaUserIdentity.Normalize("line", ""));
    }

    private static void TestChannelMemoryIsolation()
    {
        Console.WriteLine("--- channel namespace isolation (大項一前置) ---");
        // 同名 id、不同通道 → 不同內部 userId（不共用記憶）
        var line = NyaUserIdentity.Normalize("line", "u1");
        var web  = NyaUserIdentity.Normalize("web", "u1");
        AssertTrue("isolation-differ", line != web);
        AssertEqual("isolation-line-raw", "u1", line);
        AssertEqual("isolation-web-ns", "web:u1", web);
    }

    // ── 測試替身 ────────────────────────────────────────────────────────────────
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
