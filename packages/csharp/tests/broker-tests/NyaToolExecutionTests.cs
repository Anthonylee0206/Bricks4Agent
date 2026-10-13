using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

/// <summary>
/// 插件執行路徑的嚴格回歸測試（對應 nyachat_architecture_gaps 嚴審後修復的四個正確性/安全 bug）：
///   #2 壞插件不得炸毀 broker（登錄器容錯：撞名/空名/GetTools 拋例外 → 跳過、不 throw）；
///   #8 已停用工具不得被執行（執行門 = 已知 ∩ 啟用 ∩ 授權）；
///   #4 執行前依 schema 驗證 LLM 參數（required + enum）；
///   #7 多工具情境下 DirectReplies 不得被靜默吞掉。
/// </summary>
public static class NyaToolExecutionTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0;
        _failed = 0;
        Console.WriteLine("=== NyaChat Tool Execution Hardening Tests (插件嚴審修復) ===");
        Console.WriteLine();

        // #2 登錄器容錯
        TestRegistryToleratesEmptyName();
        TestRegistryToleratesDuplicateName();
        TestRegistryToleratesThrowingPlugin();

        // #8 執行門：停用工具不得執行
        TestExecutableGateRejectsDisabled();
        TestExecutableGateRejectsUnknown();
        TestExecutableGateRejectsUnauthorized();
        TestExecutableGateAllowsEnabledAuthorized();
        TestDisabledToolStillExposed();
        TestUnauthorizedToolStillExposed();

        // #4 參數驗證
        TestArgValidationMissingRequired();
        TestArgValidationEnumViolation();
        TestArgValidationValid();
        TestArgValidationNoRequiredAllowsEmpty();

        // #7 多工具 DirectReplies
        TestDirectRepliesSingle();
        TestDirectRepliesMultiAllDirect();
        TestDirectRepliesMixedFallsToClosedLoop();
        TestDirectRepliesNonePersonalized();

        // #10 例外訊息不得外洩給 LLM/使用者
        TestInternalErrorJsonIsGeneric();
        TestFactToolDoesNotLeakExceptionText();

        // 改進3：per-tool timeout（NyaToolTimeout）
        TestToolTimeoutReturnsNull();
        TestToolTimeoutFastToolPassesThrough();
        TestToolTimeoutOuterCancelPropagates();

        // 工具執行狀態標註（成功/失敗/未啟動 → 明確告知 LLM）
        TestStatusFromResultMapping();
        TestStatusLabelsAreDistinct();
        TestFormatPrependsSuccessLabel();
        TestFormatPrependsFailedLabel();
        TestFormatPrependsNotEnabledLabel();
        TestFormatLabelIsFirstLineBeforeContent();
        TestFormatEmptyContentPlaceholder();

        Console.WriteLine();
        Console.WriteLine($"=== Tool Execution Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    // ── 改進3：per-tool timeout（NyaToolTimeout）──────────────────────────────────
    private static void TestToolTimeoutReturnsNull()
    {
        Console.WriteLine("--- slow plugin hits per-tool timeout → null (caller maps to Failed) ---");
        var slow = new DelayPlugin(TimeSpan.FromSeconds(30));
        var result = NyaToolTimeout.RunAsync(slow, Ctx(), TimeSpan.FromMilliseconds(100), CancellationToken.None)
            .GetAwaiter().GetResult();
        AssertTrue("timeout-null", result == null);
    }

    private static void TestToolTimeoutFastToolPassesThrough()
    {
        Console.WriteLine("--- fast plugin finishes within timeout → result passed through ---");
        var fast = new DelayPlugin(TimeSpan.Zero);
        var result = NyaToolTimeout.RunAsync(fast, Ctx(), TimeSpan.FromSeconds(5), CancellationToken.None)
            .GetAwaiter().GetResult();
        AssertTrue("fast-passthrough", result != null && result.Success);
    }

    private static void TestToolTimeoutOuterCancelPropagates()
    {
        Console.WriteLine("--- outer cancellation propagates as OCE (not swallowed as timeout) ---");
        using var outer = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var slow = new DelayPlugin(TimeSpan.FromSeconds(30));
        var threw = false;
        try
        {
            NyaToolTimeout.RunAsync(slow, Ctx(), TimeSpan.FromSeconds(10), outer.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { threw = true; }
        AssertTrue("outer-cancel-throws", threw);
    }

    private static NyaToolContext Ctx() => new()
    {
        ToolName = "t", UserId = "u", ChannelType = "line", ArgumentsJson = "{}"
    };

    private sealed class DelayPlugin : INyaToolPlugin
    {
        private readonly TimeSpan _delay;
        public DelayPlugin(TimeSpan delay) { _delay = delay; }
        public IReadOnlyList<NyaToolSchema> GetTools() => new[] { new NyaToolSchema { Name = "t" } };
        public async Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken ct)
        {
            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, ct);
            return NyaToolResult.Ok("{}");
        }
    }

    // ── 工具執行狀態標註（NyaToolStatusFormatter）─────────────────────────────────
    private static void TestStatusFromResultMapping()
    {
        Console.WriteLine("--- FromResult maps Ok→Success, Fail→Failed (NotEnabled is gate-only) ---");
        AssertEqual("ok-success", NyaToolExecStatus.Success, NyaToolStatusFormatter.FromResult(NyaToolResult.Ok("x")));
        AssertEqual("fail-failed", NyaToolExecStatus.Failed, NyaToolStatusFormatter.FromResult(NyaToolResult.Fail("nope")));
    }

    private static void TestStatusLabelsAreDistinct()
    {
        Console.WriteLine("--- three states carry distinct 成功/失敗/未啟動 labels ---");
        AssertTrue("success-label", NyaToolStatusFormatter.Label(NyaToolExecStatus.Success).Contains("成功"));
        AssertTrue("failed-label", NyaToolStatusFormatter.Label(NyaToolExecStatus.Failed).Contains("失敗"));
        AssertTrue("notenabled-label", NyaToolStatusFormatter.Label(NyaToolExecStatus.NotEnabled).Contains("未啟動"));
    }

    private static void TestFormatPrependsSuccessLabel()
    {
        Console.WriteLine("--- success result is tagged [工具執行狀態：成功] and keeps content ---");
        var formatted = NyaToolStatusFormatter.FormatForLlm(NyaToolExecStatus.Success, "損益 +1200");
        AssertTrue("has-status-tag", formatted.Contains("[工具執行狀態：成功]"));
        AssertTrue("keeps-content", formatted.Contains("損益 +1200"));
    }

    private static void TestFormatPrependsFailedLabel()
    {
        Console.WriteLine("--- failed result is tagged 失敗 ---");
        var formatted = NyaToolStatusFormatter.FormatForLlm(NyaToolExecStatus.Failed, "{\"success\":false}");
        AssertTrue("has-failed-tag", formatted.Contains("工具執行狀態：失敗"));
    }

    private static void TestFormatPrependsNotEnabledLabel()
    {
        Console.WriteLine("--- not-enabled (disabled/unauth/unknown) result is tagged 未啟動 ---");
        var formatted = NyaToolStatusFormatter.FormatForLlm(NyaToolExecStatus.NotEnabled, "{\"status\":\"disabled\"}");
        AssertTrue("has-notenabled-tag", formatted.Contains("工具執行狀態：未啟動"));
    }

    private static void TestFormatLabelIsFirstLineBeforeContent()
    {
        Console.WriteLine("--- status label is on the first line, content follows (truncation can't cut the label) ---");
        var formatted = NyaToolStatusFormatter.FormatForLlm(NyaToolExecStatus.Success, "body");
        var firstLine = formatted.Split('\n')[0];
        AssertTrue("first-line-is-status", firstLine.StartsWith("[工具執行狀態：") && firstLine.Contains("成功"));
        AssertTrue("content-after-newline", formatted.Contains("\nbody"));
    }

    private static void TestFormatEmptyContentPlaceholder()
    {
        Console.WriteLine("--- empty tool content falls back to (no content) but still tagged ---");
        var formatted = NyaToolStatusFormatter.FormatForLlm(NyaToolExecStatus.Success, "");
        AssertTrue("tagged", formatted.Contains("[工具執行狀態：成功]"));
        AssertTrue("no-content-placeholder", formatted.Contains("(no content)"));
    }

    // ── #2 登錄器容錯 ────────────────────────────────────────────────────────────
    private static void TestRegistryToleratesEmptyName()
    {
        Console.WriteLine("--- registry skips empty-name tool without throwing (#2) ---");
        var good = Schema("ok_tool");
        var empty = Schema("");
        var registry = Registry(new AllowAllToolAuthorizer(), new FakePlugin(good, empty));
        AssertTrue("knows-good", registry.IsKnownTool("ok_tool"));
        AssertTrue("skips-empty", !registry.IsKnownTool(""));
        AssertEqual("only-one-tool", 1, registry.GetAllWithStatus().Count);
    }

    private static void TestRegistryToleratesDuplicateName()
    {
        Console.WriteLine("--- registry keeps first on duplicate name, no throw (#2) ---");
        var p1 = new FakePlugin(Schema("dup"));
        var p2 = new FakePlugin(Schema("dup"), Schema("unique"));
        var registry = Registry(new AllowAllToolAuthorizer(), p1, p2);
        AssertTrue("knows-dup", registry.IsKnownTool("dup"));
        AssertTrue("knows-unique", registry.IsKnownTool("unique"));
        AssertEqual("dup-counted-once", 2, registry.GetAllWithStatus().Count);
    }

    private static void TestRegistryToleratesThrowingPlugin()
    {
        Console.WriteLine("--- registry survives a plugin whose GetTools throws (#2) ---");
        var registry = Registry(new AllowAllToolAuthorizer(),
            new ThrowingPlugin(), new FakePlugin(Schema("survivor")));
        AssertTrue("survivor-registered", registry.IsKnownTool("survivor"));
        AssertEqual("only-survivor", 1, registry.GetAllWithStatus().Count);
    }

    // ── #8 執行門 ────────────────────────────────────────────────────────────────
    private static void TestExecutableGateRejectsDisabled()
    {
        Console.WriteLine("--- disabled tool is not executable (#8) ---");
        var registry = Registry(new AllowAllToolAuthorizer(), new FakePlugin(Schema("off", defaultEnabled: false)));
        var ok = registry.TryGetExecutablePlugin("off", "u1", "line", out _, out _, out var reason);
        AssertTrue("disabled-rejected", !ok);
        AssertTrue("disabled-reason", reason != null && reason.Contains("disabled"));
    }

    private static void TestExecutableGateRejectsUnknown()
    {
        Console.WriteLine("--- unknown tool is not executable (#8) ---");
        var registry = Registry(new AllowAllToolAuthorizer(), new FakePlugin(Schema("known")));
        var ok = registry.TryGetExecutablePlugin("ghost", "u1", "line", out _, out _, out var reason);
        AssertTrue("unknown-rejected", !ok);
        AssertTrue("unknown-reason", reason != null && reason.Contains("unknown"));
    }

    private static void TestExecutableGateRejectsUnauthorized()
    {
        Console.WriteLine("--- unauthorized tool is not executable (#8) ---");
        var registry = Registry(new DenyToolAuthorizer("secret"), new FakePlugin(Schema("secret")));
        var ok = registry.TryGetExecutablePlugin("secret", "u1", "line", out _, out _, out var reason);
        AssertTrue("unauth-rejected", !ok);
        AssertTrue("unauth-reason", reason != null && reason.Contains("authorized"));
    }

    private static void TestExecutableGateAllowsEnabledAuthorized()
    {
        Console.WriteLine("--- enabled + authorized tool is executable (#8) ---");
        var registry = Registry(new AllowAllToolAuthorizer(), new FakePlugin(Schema("go")));
        var ok = registry.TryGetExecutablePlugin("go", "u1", "line", out var plugin, out var schema, out var reason);
        AssertTrue("allowed", ok);
        AssertTrue("plugin-out", plugin != null);
        AssertTrue("schema-out", schema != null && schema.Name == "go");
        AssertTrue("no-reason", reason == null);
    }

    private static void TestDisabledToolStillExposed()
    {
        Console.WriteLine("--- disabled tool is STILL exposed to LLM (item 7) ---");
        var registry = Registry(new AllowAllToolAuthorizer(), new FakePlugin(Schema("off", defaultEnabled: false)));
        var llm = registry.BuildLlmTools("u1", "line").Select(t => t.Function.Name).ToList();
        AssertTrue("disabled-exposed", llm.Contains("off"));
        var ok = registry.TryGetExecutablePlugin("off", "u1", "line", out _, out _, out var st);
        AssertTrue("disabled-not-exec", !ok && st == "disabled");
    }

    private static void TestUnauthorizedToolStillExposed()
    {
        Console.WriteLine("--- unauthorized tool is STILL exposed to LLM (item 7) ---");
        var registry = Registry(new DenyToolAuthorizer("secret"), new FakePlugin(Schema("secret")));
        var llm = registry.BuildLlmTools("u1", "line").Select(t => t.Function.Name).ToList();
        AssertTrue("unauth-exposed", llm.Contains("secret"));
        var ok = registry.TryGetExecutablePlugin("secret", "u1", "line", out _, out _, out var st);
        AssertTrue("unauth-not-exec", !ok && st == "not_authorized");
    }

    // ── #4 參數驗證 ──────────────────────────────────────────────────────────────
    private static NyaToolSchema FactSchema() => new()
    {
        Name = "upsert_fact",
        Parameters = new[]
        {
            new NyaToolParam { Name = "category", Required = true, Enum = new[] { "identity", "preference", "context", "instruction" } },
            new NyaToolParam { Name = "key", Required = true },
            new NyaToolParam { Name = "value", Required = true },
        }
    };

    private static void TestArgValidationMissingRequired()
    {
        Console.WriteLine("--- missing required arg rejected (#4) ---");
        var (ok, err) = NyaToolArgValidator.Validate(FactSchema(), "{\"category\":\"identity\",\"key\":\"name\"}");
        AssertTrue("missing-rejected", !ok);
        AssertTrue("missing-names-value", err != null && err.Contains("value"));
    }

    private static void TestArgValidationEnumViolation()
    {
        Console.WriteLine("--- enum violation rejected (#4) ---");
        var (ok, err) = NyaToolArgValidator.Validate(FactSchema(),
            "{\"category\":\"banana\",\"key\":\"k\",\"value\":\"v\"}");
        AssertTrue("enum-rejected", !ok);
        AssertTrue("enum-names-param", err != null && err.Contains("category"));
    }

    private static void TestArgValidationValid()
    {
        Console.WriteLine("--- valid args accepted (#4) ---");
        var (ok, err) = NyaToolArgValidator.Validate(FactSchema(),
            "{\"category\":\"preference\",\"key\":\"spicy\",\"value\":\"yes\"}");
        AssertTrue("valid-accepted", ok);
        AssertTrue("valid-no-error", err == null);
    }

    private static void TestArgValidationNoRequiredAllowsEmpty()
    {
        Console.WriteLine("--- schema with no required params accepts empty/garbage args (#4) ---");
        var schema = new NyaToolSchema { Name = "noop", Parameters = Array.Empty<NyaToolParam>() };
        var (ok1, _) = NyaToolArgValidator.Validate(schema, "{}");
        var (ok2, _) = NyaToolArgValidator.Validate(schema, "not json");
        AssertTrue("empty-ok", ok1);
        AssertTrue("garbage-ok-when-no-required", ok2);
    }

    // ── #7 多工具 DirectReplies ───────────────────────────────────────────────────
    private static void TestDirectRepliesSingle()
    {
        Console.WriteLine("--- single direct-reply tool → direct output (#7) ---");
        var plan = NyaToolReplyPlanner.PlanDirectReplies(new[]
        {
            NyaToolResult.Ok("llm", new[] { "raw-a" }),
        });
        AssertTrue("single-direct", plan != null && plan.Count == 1 && plan[0] == "raw-a");
    }

    private static void TestDirectRepliesMultiAllDirect()
    {
        Console.WriteLine("--- multiple direct-reply tools → all preserved, none dropped (#7) ---");
        var plan = NyaToolReplyPlanner.PlanDirectReplies(new[]
        {
            NyaToolResult.Ok("l1", new[] { "raw-a" }),
            NyaToolResult.Ok("l2", new[] { "raw-b", "raw-c" }),
        });
        AssertTrue("multi-not-null", plan != null);
        AssertTrue("multi-keeps-all", plan != null && plan.Count == 3
            && plan[0] == "raw-a" && plan[1] == "raw-b" && plan[2] == "raw-c");
    }

    private static void TestDirectRepliesMixedFallsToClosedLoop()
    {
        Console.WriteLine("--- mixed direct + personalized → closed loop (null), no silent drop (#7) ---");
        var plan = NyaToolReplyPlanner.PlanDirectReplies(new[]
        {
            NyaToolResult.Ok("personalize-me"),            // no DirectReplies
            NyaToolResult.Ok("l2", new[] { "raw-b" }),     // direct
        });
        AssertTrue("mixed-null", plan == null);
    }

    private static void TestDirectRepliesNonePersonalized()
    {
        Console.WriteLine("--- no direct replies → closed loop (null) (#7) ---");
        var plan = NyaToolReplyPlanner.PlanDirectReplies(new[]
        {
            NyaToolResult.Ok("a"),
            NyaToolResult.Ok("b"),
        });
        AssertTrue("none-null", plan == null);
    }

    // ── #10 例外訊息不得外洩 ──────────────────────────────────────────────────────
    private static void TestInternalErrorJsonIsGeneric()
    {
        Console.WriteLine("--- internal error JSON is generic, carries no exception text (#10) ---");
        var json = NyaToolError.InternalJson();
        AssertTrue("is-failure", json.Contains("\"success\":false"));
        AssertTrue("has-generic-msg", json.Contains(NyaToolError.InternalMessage));
    }

    private static void TestFactToolDoesNotLeakExceptionText()
    {
        Console.WriteLine("--- FactToolPlugin catch sanitizes exception, no raw message leak (#10) ---");
        var path = Path.Combine(Path.GetTempPath(), $"nya_toolexec_{Guid.NewGuid():N}.db");
        var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        try
        {
            var store = new FixedConfigStore(new NyaChatConfig());
            var mem = new NyaMemoryService(db, store, NullLogger<NyaMemoryService>.Instance);
            var audit = new NyaAuditLogger(db, store, NullLogger<NyaAuditLogger>.Instance);
            var plugin = new FactToolPlugin(mem, audit, NullLogger<FactToolPlugin>.Instance);

            // 觸發 try 內部的 JsonDocument.Parse 例外（壞 JSON），驗證 catch 回傳泛用訊息而非 ex.Message。
            var ctx = new NyaToolContext
            {
                ToolName = "upsert_fact",
                UserId = "u_leak",
                ChannelType = "line",
                ArgumentsJson = "{ this is not valid json"
            };
            var result = plugin.ExecuteAsync(ctx, CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue("leak-failed", !result.Success);
            AssertEqual("leak-sanitized", NyaToolError.InternalJson(), result.LlmContent);
            // 回饋內容不得含 System.Text.Json 解析器的原始用語。
            AssertTrue("leak-no-raw-parser-text",
                !result.LlmContent.Contains("JSON") && !result.LlmContent.Contains("LineNumber"));
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }

    // ── 測試替身與工廠 ────────────────────────────────────────────────────────────
    private static NyaToolRegistry Registry(INyaToolAuthorizer auth, params INyaToolPlugin[] plugins)
    {
        var enableStore = new NyaToolEnableStore(new ConfigurationBuilder().Build(), NullLogger<NyaToolEnableStore>.Instance);
        return new NyaToolRegistry(plugins, auth, enableStore, NullLogger<NyaToolRegistry>.Instance);
    }

    private static NyaToolSchema Schema(string name, bool defaultEnabled = true) => new()
    {
        Name = name, Description = name + " desc", DefaultEnabled = defaultEnabled
    };

    private sealed class FakePlugin : INyaToolPlugin
    {
        private readonly NyaToolSchema[] _schemas;
        public FakePlugin(params NyaToolSchema[] schemas) { _schemas = schemas; }
        public IReadOnlyList<NyaToolSchema> GetTools() => _schemas;
        public Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken ct)
            => Task.FromResult(NyaToolResult.Ok("{}"));
    }

    private sealed class ThrowingPlugin : INyaToolPlugin
    {
        public IReadOnlyList<NyaToolSchema> GetTools() => throw new InvalidOperationException("boom");
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
