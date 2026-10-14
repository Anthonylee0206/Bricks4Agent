using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

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
        TestUserIdentityNormalize();
        TestChannelMemoryIsolation();

        Console.WriteLine();
        Console.WriteLine($"=== Token Budget Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static void TestEstimatorBasics()
    {
        Console.WriteLine("--- NyaTokenEstimator basics ---");
        var est = new NyaTokenEstimator(1.5, 0.25);
        AssertTrue("empty-zero", est.Estimate("") == 0);

        AssertTrue("cjk-heavier", est.Estimate("你好世界你好") > est.Estimate("hello!"));

        AssertTrue("monotonic", est.Estimate("一二三四五") >= est.Estimate("一二"));
    }

    private static void TestEstimatorTruncate()
    {
        Console.WriteLine("--- NyaTokenEstimator truncate ---");
        var est = new NyaTokenEstimator(1.5, 0.25);
        var longText = new string('字', 500);
        var cut = est.TruncateToTokens(longText, 100);
        AssertTrue("truncated-shorter", cut.Length < longText.Length);
        AssertTrue("truncated-marker", cut.Contains("截斷"));
        AssertTrue("truncated-under-budget", est.Estimate(cut) <= 100 + 20 );

        AssertEqual("no-truncate", "短", est.TruncateToTokens("短", 100));
    }

    private static NyaPromptBuilder BuildBuilder()
    {
        var store = new FixedConfigStore(new NyaChatConfig());
        var enableStore = new NyaToolEnableStore(
            new ConfigurationBuilder().Build(), NullLogger<NyaToolEnableStore>.Instance);
        var registry = new NyaToolRegistry(
            Array.Empty<INyaToolPlugin>(), new AllowAllToolAuthorizer(), enableStore,
            NullLogger<NyaToolRegistry>.Instance);

        var templates = new NyaPromptTemplateProvider(store, NullLogger<NyaPromptTemplateProvider>.Instance);
        return new NyaPromptBuilder(store, registry, templates);
    }

    private static SoulDefinition Soul() => new() { SoulId = "default", DisplayName = "NYA" };

    private static void TestUserIdentityNormalize()
    {
        Console.WriteLine("--- NyaUserIdentity.Normalize (大項一前置) ---");

        AssertEqual("line-keeps-id", "U123abc", NyaUserIdentity.Normalize("line", "U123abc"));
        AssertEqual("line-case-insensitive", "U123abc", NyaUserIdentity.Normalize("LINE", "U123abc"));

        AssertEqual("discord-prefixed", "discord:u1", NyaUserIdentity.Normalize("discord", "u1"));
        AssertEqual("web-prefixed", "web:u1", NyaUserIdentity.Normalize("Web", "u1"));

        AssertEqual("empty-safe", "", NyaUserIdentity.Normalize("line", ""));
    }

    private static void TestChannelMemoryIsolation()
    {
        Console.WriteLine("--- channel namespace isolation (大項一前置) ---");

        var line = NyaUserIdentity.Normalize("line", "u1");
        var web  = NyaUserIdentity.Normalize("web", "u1");
        AssertTrue("isolation-differ", line != web);
        AssertEqual("isolation-line-raw", "u1", line);
        AssertEqual("isolation-web-ns", "web:u1", web);
    }

    private sealed class FixedConfigStore : INyaConfigStore<NyaChatConfig>
    {
        private readonly NyaChatConfig _c;
        public FixedConfigStore(NyaChatConfig c) { _c = c; }
        public NyaChatConfig Current => _c;
        public Task UpdateAsync(NyaChatConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
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
