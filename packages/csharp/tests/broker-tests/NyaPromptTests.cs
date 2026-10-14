using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

public static class NyaPromptTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0;
        _failed = 0;

        Console.WriteLine("=== NyaChat Prompt Maintainability Tests (大項五) ===");
        Console.WriteLine();

        TestRenderPlaceholders();
        TestSectionFallbackWhenDirMissing();
        TestExtractionSummarizationFallback();
        TestCapabilitiesDynamicFromTools();
        TestCapabilitiesEmptyWhenNoTools();
        TestNoHardcodedTradeLine();
        TestAllToolsExposedUsableIsSubset();
        TestSystemPromptTaskLayerLast();
        TestTaskLayerProceduralClosing();
        TestToolResultDirective();
        TestTaskClosingFlagsToolHallucination();
        TestAvailableTopicsInjectedWhenTopicToolPresent();

        Console.WriteLine();
        Console.WriteLine($"=== Prompt Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static void TestRenderPlaceholders()
    {
        Console.WriteLine("--- template Render placeholders ---");
        var rendered = NyaPromptTemplateProvider.Render("你好 {name}，今天 {date}",
            new Dictionary<string, string> { ["name"] = "老闆", ["date"] = "週一" });
        AssertEqual("render-substitutes", "你好 老闆，今天 週一", rendered);

        var unknown = NyaPromptTemplateProvider.Render("hi {missing}", new Dictionary<string, string> { ["x"] = "1" });
        AssertEqual("render-keeps-unknown", "hi {missing}", unknown);
    }

    private static void TestSectionFallbackWhenDirMissing()
    {
        Console.WriteLine("--- template fallback when prompts dir missing ---");
        var tpl = Templates("___no_such_prompts_dir___");

        var limitHeader = tpl.Section("system", "capabilities_limit_header");
        AssertTrue("fallback-nonempty", !string.IsNullOrWhiteSpace(limitHeader));
        var limits = tpl.List("system", "global_limits");
        AssertTrue("fallback-capabilities-come-from-registry", limits.Count == 0);

        AssertTrue("fallback-no-trade-limit", limits.All(l => !l.Contains("交易") && !l.Contains("付款")));
    }

    private static void TestExtractionSummarizationFallback()
    {
        Console.WriteLine("--- extraction/summarization template fallback ---");
        var tpl = Templates("___no_such_prompts_dir___");

        AssertTrue("summary-fresh", tpl.Section("summarization", "intro_fresh").Contains("摘要引擎"));
        AssertTrue("summary-requirements", tpl.Section("summarization", "requirements").Contains("繁體中文"));
    }

    private static void TestCapabilitiesDynamicFromTools()
    {
        Console.WriteLine("--- capabilities generated from usable tools ---");
        var search = Schema("search", "我可以幫你做即時網路搜尋並彙整結果。", group: "search");
        var system = BuildSystem(new AllowAllToolAuthorizer(), search);
        AssertTrue("cap-has-can-header", system.Contains("你目前具備以下能力"));
        AssertTrue("cap-has-statement", system.Contains("我可以幫你做即時網路搜尋並彙整結果。"));
        AssertTrue("cap-has-limit-header", system.Contains("能力邊界"));
    }

    private static void TestCapabilitiesEmptyWhenNoTools()
    {
        Console.WriteLine("--- no can-header when no usable tools ---");
        var system = BuildSystem(new AllowAllToolAuthorizer() );
        AssertTrue("no-can-header", !system.Contains("你目前具備以下能力"));

        AssertTrue("still-has-limits", system.Contains("能力邊界"));
    }

    private static void TestNoHardcodedTradeLine()
    {
        Console.WriteLine("--- hardcoded trade/payment denial removed (5E) ---");
        var system = BuildSystem(new AllowAllToolAuthorizer());
        AssertTrue("no-trade-denial", !system.Contains("無法執行交易"));
        AssertTrue("no-payment-denial", !system.Contains("付款"));
    }

    private static void TestAllToolsExposedUsableIsSubset()
    {
        Console.WriteLine("--- BuildLlmTools exposes only usable tools; unusable ones are listed as closed ---");
        var a = Schema("search", "搜尋能力", group: "search");
        var b = Schema("rail", "台鐵查詢能力", group: "transport");
        var registry = Registry(new DenyToolAuthorizer("rail"), new FakePlugin(a, b));
        var usable = registry.GetUsableSchemas("u1", "line").Select(s => s.Name).OrderBy(x => x).ToList();
        var llm = registry.BuildLlmTools("u1", "line").Select(t => t.Function.Name).OrderBy(x => x).ToList();
        AssertTrue("llm-excludes-unauthorized-rail", !llm.Contains("rail"));
        AssertTrue("unusable-listed-with-reason", registry.GetUnavailableSchemas("u1", "line").Any(u => u.Schema.Name == "rail" && u.Reason == "not_authorized"));
        AssertTrue("llm-includes-search", llm.Contains("search"));
        AssertTrue("usable-excludes-rail", !usable.Contains("rail"));
        AssertTrue("usable-subset", usable.All(llm.Contains));
    }

    private static void TestSystemPromptTaskLayerLast()
    {
        Console.WriteLine("--- system prompt: task layer comes last, after persona + memory ---");
        var facts = new List<NyaNote> { new() { Key = "drink", Content = "拿鐵", IsCurrent = true } };
        var summaries = new List<NyaSummary> { new() { SummaryText = "上次聊到考試", CoveredFromSeq = 1, IsActive = true } };
        var sys = BuildSystemWithMemory(facts, summaries);

        var idIdx    = sys.IndexOf("<system_identity>", StringComparison.Ordinal);
        var factsIdx = sys.IndexOf("<long_term_notes>", StringComparison.Ordinal);
        var memIdx   = sys.IndexOf("<long_term_memory>", StringComparison.Ordinal);
        var taskIdx  = sys.IndexOf("<task>", StringComparison.Ordinal);

        AssertTrue("has-task-layer", taskIdx >= 0);
        AssertTrue("persona-before-task", idIdx >= 0 && idIdx < taskIdx);
        AssertTrue("facts-before-task", factsIdx >= 0 && factsIdx < taskIdx);
        AssertTrue("summaries-before-task", memIdx >= 0 && memIdx < taskIdx);
        AssertTrue("task-is-last", taskIdx > factsIdx && taskIdx > memIdx);
        AssertTrue("no-old-instructions-tag", !sys.Contains("<system_instructions>"));
    }

    private static void TestTaskLayerProceduralClosing()
    {
        Console.WriteLine("--- system prompt: task layer has procedural 'what to do now' + output spec ---");
        var sys = BuildSystemWithMemory(new List<NyaNote>(), new List<NyaSummary>());
        AssertTrue("has-now-directive", sys.Contains("你現在要做的"));
        AssertTrue("has-output-spec", sys.Contains("輸出"));
        AssertTrue("dynamic-time-excluded-from-prefix", !sys.Contains("現在時間"));
    }

    private static void TestToolResultDirective()
    {
        Console.WriteLine("--- closed-loop tool-result directive: persona transcribe, do not alter numbers ---");
        var store = new FixedConfigStore(new NyaChatConfig());
        var registry = Registry(new AllowAllToolAuthorizer(), new FakePlugin());
        var templates = new NyaPromptTemplateProvider(store, NullLogger<NyaPromptTemplateProvider>.Instance);
        var builder = new NyaPromptBuilder(store, registry, templates);

        var directive = builder.ToolResultDirective();
        AssertTrue("directive-nonempty", !string.IsNullOrWhiteSpace(directive));
        AssertTrue("directive-no-alter", directive.Contains("不要更改"));
        AssertTrue("directive-no-fabricate", directive.Contains("捏造"));

        AssertTrue("directive-explains-status", directive.Contains("completed") && directive.Contains("needs_confirmation") && directive.Contains("noop") && directive.Contains("partial"));
        AssertTrue("directive-flags-hallucination", directive.Contains("只有 ok=true 且 outcome=completed"));
    }

    private static void TestTaskClosingFlagsToolHallucination()
    {
        Console.WriteLine("--- task layer warns: claiming tool use without an execution result is hallucination ---");
        var sys = BuildSystemWithMemory(new List<NyaNote>(), new List<NyaSummary>());
        AssertTrue("task-flags-hallucination", sys.Contains("胡言亂語"));
        AssertTrue("task-ties-to-result", sys.Contains("執行結果"));
    }

    private static void TestAvailableTopicsInjectedWhenTopicToolPresent()
    {
        Console.WriteLine("--- available topics injected only when topic tool present (item 8) ---");
        var store = new FixedConfigStore(new NyaChatConfig());
        var templates = Templates();

        var regWith = Registry(new AllowAllToolAuthorizer(), new FakePlugin(Schema("switch_topic", "切換話題", group: "topic")));
        var builderWith = new NyaPromptBuilder(store, regWith, templates);
        var topics = new List<NyaTopicEntry>
        {
            new() { TopicId = "t_1", Title = "投資", IsActive = true },
            new() { TopicId = "t_2", Title = "閒聊", IsActive = false },
        };
        var msgs = builderWith.Build("hi", Soul(), new List<NyaNote>(), new List<NyaSummary>(),
            new List<NyaMessage>(), NyaPromptBudget.Unlimited, NyaTokenEstimator.Default, "u1", "line",
            topics, "t_1", out _);
        var sys = msgs[0].Content;
        AssertTrue("topics-header", sys.Contains("switch_topic") && sys.Contains("聊天室"));
        AssertTrue("topics-list-t1", sys.Contains("t_1") && sys.Contains("投資"));
        AssertTrue("topics-current-marker", sys.Contains("（目前）"));

        var regNo = Registry(new AllowAllToolAuthorizer(), new FakePlugin(Schema("search", "搜尋", group: "search")));
        var builderNo = new NyaPromptBuilder(store, regNo, templates);
        var msgs2 = builderNo.Build("hi", Soul(), new List<NyaNote>(), new List<NyaSummary>(),
            new List<NyaMessage>(), NyaPromptBudget.Unlimited, NyaTokenEstimator.Default, "u1", "line",
            topics, "t_1", out _);
        AssertTrue("no-topics-section", !msgs2[0].Content.Contains("available_topics"));
    }

    private static NyaPromptTemplateProvider Templates()
    {
        var store = new FixedConfigStore(new NyaChatConfig());
        return new NyaPromptTemplateProvider(store, NullLogger<NyaPromptTemplateProvider>.Instance);
    }

    private static NyaPromptTemplateProvider Templates(string promptsDir)
    {
        var store = new FixedConfigStore(new NyaChatConfig { PromptsDirectory = promptsDir });
        return new NyaPromptTemplateProvider(store, NullLogger<NyaPromptTemplateProvider>.Instance);
    }

    private static NyaToolRegistry Registry(INyaToolAuthorizer auth, params INyaToolPlugin[] plugins)
    {
        var enableStore = new NyaToolEnableStore(new ConfigurationBuilder().Build(), NullLogger<NyaToolEnableStore>.Instance);
        return new NyaToolRegistry(plugins, auth, enableStore, NullLogger<NyaToolRegistry>.Instance);
    }

    private static string BuildSystem(INyaToolAuthorizer auth, params NyaToolSchema[] schemas)
    {
        var store = new FixedConfigStore(new NyaChatConfig());
        var registry = Registry(auth, schemas.Length > 0 ? new FakePlugin(schemas) : new FakePlugin());
        var templates = new NyaPromptTemplateProvider(store, NullLogger<NyaPromptTemplateProvider>.Instance);
        var builder = new NyaPromptBuilder(store, registry, templates);
        var msgs = builder.Build("hi", Soul(), new List<NyaNote>(), new List<NyaSummary>(), new List<NyaMessage>(), "u1", "line");
        return msgs[0].Content;
    }

    private static string BuildSystemWithMemory(List<NyaNote> facts, List<NyaSummary> summaries)
    {
        var store = new FixedConfigStore(new NyaChatConfig());
        var registry = Registry(new AllowAllToolAuthorizer(), new FakePlugin());
        var templates = new NyaPromptTemplateProvider(store, NullLogger<NyaPromptTemplateProvider>.Instance);
        var builder = new NyaPromptBuilder(store, registry, templates);
        var msgs = builder.Build("hi", Soul(), facts, summaries, new List<NyaMessage>(), "u1", "line");
        return msgs[0].Content;
    }

    private static SoulDefinition Soul() => new() { SoulId = "default", DisplayName = "NYA" };

    private static NyaToolSchema Schema(string name, string capability, string group) => new()
    {
        Name = name, Description = name + " desc", Group = group,
        DefaultEnabled = true, CapabilityStatement = capability
    };

    private sealed class FakePlugin : INyaToolPlugin
    {
        private readonly NyaToolSchema[] _schemas;
        public FakePlugin(params NyaToolSchema[] schemas) { _schemas = schemas; }
        public IReadOnlyList<NyaToolSchema> GetTools() => _schemas;
        public Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken ct)
            => Task.FromResult(NyaToolResult.Ok("{}"));
    }

    private sealed class DenyToolAuthorizer : INyaToolAuthorizer
    {
        private readonly string _deny;
        public DenyToolAuthorizer(string deny) { _deny = deny; }
        public bool CanUse(string userId, string channelType, NyaToolSchema tool)
            => !string.Equals(tool.Name, _deny, StringComparison.OrdinalIgnoreCase);
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
