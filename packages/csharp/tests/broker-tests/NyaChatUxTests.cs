using System.Reflection;
using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

public static class NyaChatUxTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0;
        _failed = 0;
        Console.WriteLine("=== NyaChat UX Tests ===");
        TestDetector();
        TestCorrections();
        TestCorrectiveExecution();
        TestStatusFallbacks();
        TestLedgerAcrossRounds();
        TestDirectReplies();
        TestCompleteHistory();
        TestTurnTrace();
        Console.WriteLine($"=== NyaChat UX Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static IReadOnlyList<string> Detect(string text)
        => (IReadOnlyList<string>)typeof(NyaToolReplyPlanner).GetMethod("DetectCompletedActions", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { text })!;

    private static void TestDetector()
    {
        foreach (var (text, action) in new[]
        {
            ("已記住您的喜好。", "記住"), ("記下來了！", "記住"), ("我已經幫你記下。", "記住"),
            ("已刪除那條記錄。", "刪除"), ("已移除那條記錄。", "刪除"), ("已建立新話題。", "建立"),
            ("已切換話題。", "切換"), ("已開新話題。", "開新話題"), ("已幫您查到資料。", "查詢"),
            ("已派遣研究代理。", "派遣"), ("已設定提醒。", "設定"), ("已重新命名話題。", "改名")
        })
            AssertTrue($"detect-{text}", Detect(text).Contains(action));

        foreach (var text in new[]
        {
            "要我幫您記住嗎", "還沒刪除", "尚未移除。", "已刪除嗎？", "是否已記住？", "不是已設定。",
            "我沒有說已派遣代理。", "如果已建立，就能繼續。", "您要不要問他已切換了嗎？"
        })
            AssertTrue($"ignore-{text}", Detect(text).Count == 0);

        AssertTrue("detect-positive-after-negation", Detect("還沒刪除，但已記住。").SequenceEqual(new[] { "記住" }));
        AssertTrue("detect-positive-with-negated-object", Detect("已刪除沒有用的記錄。").Contains("刪除"));
        AssertTrue("deduplicate-claims", Detect("已記住。記下來了。").Count == 1);
    }

    private static void TestCorrections()
    {
        using (var f = new Fixture(new[] { Reply("已派遣研究代理。"), Reply("我可以協助你整理研究方向。") }))
        {
            var result = f.Chat();
            AssertEqual("corrected-reply", "我可以協助你整理研究方向。", Join(result));
            AssertEqual("one-correction", 2, f.Provider.Messages.Count);
            AssertTrue("corrective-directive", f.Provider.Messages[1].Any(m => m.Role == "system" && m.Content.Contains("本輪沒有成功執行以下動作：派遣")));
            AssertTrue("correction-offers-tools", f.Provider.ToolCounts[1] > 0);
            AssertEqual("only-corrected-reply-persisted", Join(result), f.History(result).Last().Content);
        }
        using (var f = new Fixture(new[] { Reply("已刪除。"), Reply("已移除。已記住。") }))
        {
            var result = f.Chat();
            AssertTrue("persistent-hallucination-safe", Detect(Join(result)).Count == 0);
            AssertTrue("persistent-hallucination-no-tools", Join(result).Contains("沒有執行任何工具"));
            AssertEqual("no-third-correction", 2, f.Provider.Messages.Count);
        }
        foreach (var correction in new NyaLlmResponse?[] { null, Reply("<think>已刪除</think>") })
        {
            using var f = new Fixture(new[] { Reply("已記住。"), correction });
            var result = f.Chat();
            AssertTrue("missing-correction-safe", result.Error != null && Join(result).Length > 0);
        }
        using (var f = new Fixture(new[] { Reply("要我幫您記住嗎？還沒刪除。") }))
        {
            f.Chat();
            AssertEqual("question-no-correction", 1, f.Provider.Messages.Count);
        }
        using (var f = new Fixture(new[] { Reply("<think>已派遣代理</think>我可以陪你研究。") }))
        {
            AssertEqual("guard-checks-delivered-text", "我可以陪你研究。", Join(f.Chat()));
            AssertEqual("hidden-thinking-no-correction", 1, f.Provider.Messages.Count);
        }
        using (var f = new Fixture(new[] { Reply(new string('甲', 39) + "已記住。"), Reply("我可以幫你記下喜好。") }, maxReplyLength: 40))
            AssertEqual("claim-across-chunk-boundary-corrected", "我可以幫你記下喜好。", Join(f.Chat()));
    }

    private static void TestCorrectiveExecution()
    {
        var args = "{\"key\":\"tea\",\"content\":\"喜歡茶\"}";
        using (var f = new Fixture(new[] { Reply("已記住。"), Calls(Call("note_save", args)), Reply("已記住你的喜好。") }))
        {
            var result = f.Chat();
            AssertEqual("corrective-tool-confirmation", "已記住你的喜好。", Join(result));
            AssertTrue("corrective-tool-wrote-fact", f.Memory.ListNotes(Fixture.UserId).Any(x => x.Key == "tea"));
            AssertEqual("correction-tool-followup", 3, f.Provider.Messages.Count);
            AssertTrue("corrective-tool-real-status", f.Provider.Messages[2].Any(m => m.Role == "tool" && m.Content.Contains("\"ok\":true")));
        }
        var search = new Plugin("search", NyaToolResult.Ok("找到三筆資料"));
        using (var f = new Fixture(new[] { Calls(Call("search")), Reply("已幫您查到資料，已記住喜好。"), Reply("已幫您查到資料。") }, new[] { search }))
        {
            AssertEqual("unrelated-success-does-not-ground-memory", "已幫您查到資料。", Join(f.Chat()));
            AssertTrue("only-unbacked-action-in-directive", f.Provider.Messages[2].Any(m => m.Role == "system" && m.Content.StartsWith("本輪沒有成功執行以下動作：記住。", StringComparison.Ordinal)));
        }
    }

    private static void TestStatusFallbacks()
    {
        foreach (var (status, expected) in new[]
        {
            (NyaToolExecStatus.Success, "操作成功"), (NyaToolExecStatus.Failed, "操作失敗"),
            (NyaToolExecStatus.Denied, "因權限不足未執行"), (NyaToolExecStatus.NotEnabled, "未啟用或不存在")
        })
            AssertTrue($"fallback-{status}", NyaToolStatusFormatter.FormatFallback(new[] { status }).Contains(expected));

        var plugins = new[]
        {
            new Plugin("search", NyaToolResult.Ok("三筆資料")),
            new Plugin("delete_record", NyaToolResult.Fail("失敗")),
            new Plugin("dispatch_agent", NyaToolResult.Ok("不應執行"), enabled: false),
            new Plugin("set_reminder", NyaToolResult.Ok("不應執行"))
        };
        using (var f = new Fixture(new NyaLlmResponse?[] { Calls(plugins.Select(p => Call(p.Name)).ToArray()), null }, plugins, deny: "set_reminder"))
        {
            var result = f.Chat();
            var text = Join(result);
            AssertTrue("mixed-fallback-success", text.Contains("1 項操作成功"));
            AssertTrue("mixed-fallback-failure", text.Contains("1 項操作失敗"));
            AssertTrue("mixed-fallback-denied", text.Contains("1 項因權限不足未執行"));
            AssertTrue("mixed-fallback-disabled", text.Contains("1 項因工具未啟用或不存在而未執行"));
            AssertTrue("no-unconditional-completion", !text.Contains("操作執行完成"));
            AssertEqual("disabled-plugin-not-executed", 0, plugins[2].Executions);
            AssertEqual("denied-plugin-not-executed", 0, plugins[3].Executions);
            AssertTrue("denied-status-in-llm", f.Provider.Messages[1].Any(m => m.Name == "set_reminder" && m.Content.Contains("沒有權限")));
        }
        foreach (var state in new[] { "failed", "disabled", "denied", "throws" })
        {
            var plugin = new Plugin("delete_record", state == "failed" ? NyaToolResult.Fail("失敗") : NyaToolResult.Ok("ok"), enabled: state != "disabled", throws: state == "throws");
            using var f = new Fixture(new[] { Calls(Call(plugin.Name)), Reply("已刪除那條記錄。"), Reply("已刪除。") }, new[] { plugin }, deny: state == "denied" ? plugin.Name : null);
            var result = f.Chat();
            AssertTrue($"unsuccessful-{state}-cannot-ground", Detect(Join(result)).Count == 0);
            AssertEqual($"unsuccessful-{state}-single-correction", 3, f.Provider.Messages.Count);
        }
        using (var f = new Fixture(new NyaLlmResponse?[] { Calls(Call("unknown_tool")), null }))
            AssertTrue("unknown-fallback", Join(f.Chat()).Contains("未啟用或不存在"));
        using (var f = new Fixture(new NyaLlmResponse?[] { Calls(Call("note_save", "{}")), null }))
            AssertTrue("invalid-args-fallback", Join(f.Chat()).Contains("操作失敗"));
    }

    private static void TestLedgerAcrossRounds()
    {
        var plugins = new[] { new Plugin("delete_record", NyaToolResult.Ok("ok")), new Plugin("search", NyaToolResult.Ok("三筆資料")) };
        using (var f = new Fixture(new[] { Calls(Call("delete_record")), Calls(Call("search")), Reply("已刪除記錄，已幫您查到資料。") }, plugins))
        {
            AssertEqual("ledger-retains-earlier-round", "已刪除記錄，已幫您查到資料。", Join(f.Chat()));
            AssertEqual("grounded-multi-round-no-correction", 3, f.Provider.Messages.Count);
        }
        using (var f = new Fixture(new[] { Calls(Call("delete_record")), Reply("已刪除。"), Reply("已刪除。"), Reply("仍需呼叫刪除工具。") }, plugins))
        {
            f.Chat();
            var second = f.Chat();
            AssertEqual("ledger-does-not-leak-between-turns", "仍需呼叫刪除工具。", Join(second));
            AssertEqual("second-turn-needs-correction", 4, f.Provider.Messages.Count);
        }
        var cappedPlugins = new[] { new Plugin("delete_record", NyaToolResult.Ok("ok")), new Plugin("search", NyaToolResult.Ok("三筆資料")) };
        var cappedResponse = Calls(Call("delete_record"));
        cappedResponse.Content = "已刪除。";
        using (var f = new Fixture(new[] { Calls(Call("search")), cappedResponse, Reply("已刪除。") }, cappedPlugins, maxToolRounds: 1))
        {
            var result = f.Chat();
            AssertEqual("capped-unexecuted-tool-not-grounded", 0, cappedPlugins[0].Executions);
            AssertTrue("capped-tool-claim-safe", Detect(Join(result)).Count == 0);
        }
    }

    private static void TestDirectReplies()
    {
        const string raw = "目前沒有回測紀錄。\n損益 +1234.56，筆數 0。";
        var plugins = new[] { new Plugin("query_backtests", NyaToolResult.Ok(raw, new[] { raw, "第二段：2026-10-14" })) };
        using (var f = new Fixture(new[] { Calls(Call(plugins[0].Name)) }, plugins))
        {
            var result = f.Chat("陪我看看回測結果");
            AssertEqual("direct-verbatim", raw + "\n第二段：2026-10-14", Join(result));
            AssertEqual("direct-no-extra-llm", 1, f.Provider.Messages.Count);
            AssertEqual("direct-all-chunks-persisted", Join(result), f.History(result).Last().Content);
        }
        using (var f = new Fixture(new[] { Calls(Call(plugins[0].Name)) }, plugins))
            AssertEqual("machine-turn-raw", raw + "\n第二段：2026-10-14", Join(f.Chat("/backtests")));
        var exact = new Plugin("query_backtests", NyaToolResult.Ok(raw, new[] { raw }, requiresExactOutput: true));
        using (var f = new Fixture(new[] { Calls(Call(exact.Name)) }, new[] { exact }))
            AssertEqual("exact-turn-raw", raw, Join(f.Chat("陪我看看回測結果")));
        var other = new Plugin("search", NyaToolResult.Ok("搜尋結果有 3 筆"));
        using (var f = new Fixture(new[] { Calls(Call(exact.Name), Call(other.Name)), Reply("我也找到三筆搜尋資料。") }, new[] { exact, other }))
        {
            var result = f.Chat();
            AssertEqual("mixed-exact-keeps-data", "我也找到三筆搜尋資料。\n" + raw, Join(result));
            AssertTrue("mixed-exact-not-rewritten-by-llm", f.Provider.Messages[1].Any(m => m.Name == exact.Name && m.Content.Contains("請勿重述或改寫")));
            AssertEqual("mixed-exact-all-persisted", Join(result), f.History(result).Last().Content);
        }
        var second = new Plugin("query_second", NyaToolResult.Ok("筆數 2", new[] { "筆數 2" }));
        using (var f = new Fixture(new[] { Calls(Call(plugins[0].Name), Call(second.Name)) }, new[] { plugins[0], second }))
        {
            var text = Join(f.Chat());
            AssertTrue("multi-tool-no-canned-lead-in", !text.Contains("來，一起看看結果"));
            AssertTrue("multi-tool-all-data", text.EndsWith("第二段：2026-10-14\n筆數 2", StringComparison.Ordinal));
        }
        var hallucinating = new Plugin("query_backtests", NyaToolResult.Ok("已派遣研究代理。", new[] { "已派遣研究代理。" }));
        using (var f = new Fixture(new[] { Calls(Call(hallucinating.Name)), Reply("目前沒有派遣代理。") }, new[] { hallucinating }))
        {
            AssertEqual("direct-claims-also-guarded", "目前沒有派遣代理。", Join(f.Chat()));
            AssertTrue("direct-correction-has-tool-context", f.Provider.Messages[1].Any(m => m.Role == "tool" && m.Name == hallucinating.Name));
        }
    }

    private static void TestCompleteHistory()
    {
        var text = new string('甲', 100) + "\n" + new string('乙', 100) + "\n最後一段資料。";
        using var f = new Fixture(new[] { Reply(text), Reply("收到。"), Reply("摘要包含最後一段資料。") }, maxReplyLength: 40);
        var first = f.Chat();
        AssertTrue("delivery-still-chunked", first.Replies.Count > 2);
        AssertEqual("llm-full-history", Join(first), f.History(first).Last().Content);
        AssertTrue("llm-tail-in-history", f.History(first).Last().Content.Contains("最後一段資料"));
        f.Chat();
        AssertTrue("next-turn-context-has-tail", f.Provider.Messages[1].Any(m => m.Role == "assistant" && m.Content.Contains("最後一段資料")));
        f.Summarize(first.TopicId!);
        AssertTrue("summary-context-has-tail", f.Provider.Messages[2].Any(m => m.Content.Contains("最後一段資料")));
    }

    private static void TestTurnTrace()
    {
        using var f = new Fixture(new[] { Calls(Call("trace_tool")), Reply("這是工具結果。") },
            new[] { new Plugin("trace_tool", NyaToolResult.Ok("a result")), new Plugin("off_tool", NyaToolResult.Ok("hidden"), enabled: false), new Plugin("denied_tool", NyaToolResult.Ok("hidden")) }, deny: "denied_tool");
        var room = f.Memory.CreateNewTopic(Fixture.UserId, "Trace room");
        var old = f.Memory.AppendMessage(Fixture.UserId, "user", "old room input", room.TopicId);
        f.Memory.AppendMessage(Fixture.UserId, "assistant", "old room output", room.TopicId);
        f.Memory.SaveNote(new NyaToolContext { UserId = Fixture.UserId }, null, "nickname", "Alice", "never", null, []);
        f.Memory.CreateSummary(Fixture.UserId, room.TopicId, "disabled summary", 1, 2, 2);
        var result = f.Chat();
        var persisted = f.History(result).TakeLast(2).ToList();
        var traceIds = persisted.Select(m => System.Text.Json.JsonDocument.Parse(m.MetadataJson!).RootElement.GetProperty("trace_id").GetString()).ToArray();
        AssertTrue("trace-both-messages", traceIds.Length == 2 && !string.IsNullOrWhiteSpace(traceIds[0]) && traceIds[0] == traceIds[1]);
        var chain = f.Audit.GetByTraceId(traceIds[0]!);
        AssertTrue("trace-input-tools-output", new[] { "chat_input", "tool_call", "tool_result", "chat_output" }.All(a => chain.Any(l => l.Action == a)));
        AssertEqual("trace-each-model-request", 2, chain.Count(l => l.Action == "llm_request"));
        var input = chain.Single(l => l.Action == "chat_input");
        using var json = System.Text.Json.JsonDocument.Parse(input.DetailJson);
        var selection = json.RootElement.GetProperty("selection");
        AssertEqual("trace-room-saved", room.TopicId, selection.GetProperty("topic_id").GetString());
        AssertTrue("trace-history-id-saved", selection.GetProperty("history").EnumerateArray().Any(m => m.GetProperty("message_id").GetString() == old.MessageId));
        AssertTrue("trace-note-version-and-content", selection.GetProperty("notes").EnumerateArray().Any(n => n.GetProperty("key").GetString() == "nickname" && n.GetProperty("version").GetInt32() == 1 && n.GetProperty("value").GetString() == "Alice"));
        AssertEqual("trace-disabled-summaries-empty", 0, selection.GetProperty("summaries").GetArrayLength());
        AssertTrue("trace-unavailable-reasons", selection.GetProperty("unavailable_tools").EnumerateArray().Any(t => t.GetProperty("name").GetString() == "off_tool" && t.GetProperty("reason").GetString() == "disabled") && selection.GetProperty("unavailable_tools").EnumerateArray().Any(t => t.GetProperty("name").GetString() == "denied_tool" && t.GetProperty("reason").GetString() == "not_authorized"));
        AssertEqual("trace-profile-saved", "chat", json.RootElement.GetProperty("execution").GetProperty("profile").GetProperty("id").GetString());
        var savedNote = f.Memory.ListNotes(Fixture.UserId).Single();
        f.Memory.SaveNote(new NyaToolContext { UserId = Fixture.UserId }, savedNote.NoteId, null, "Bob", "keep", null, []);
        f.Memory.DeleteAllMessages(Fixture.UserId);
        AssertEqual("trace-snapshot-survives-memory-changes", input.DetailJson, f.Audit.GetByTraceId(traceIds[0]!).Single(l => l.Action == "chat_input").DetailJson);
    }

    private static NyaLlmResponse Reply(string text) => new() { Content = text };
    private static NyaToolCall Call(string name, string args = "{}") => new() { Id = Guid.NewGuid().ToString("N"), FunctionName = name, FunctionArguments = args };
    private static NyaLlmResponse Calls(params NyaToolCall[] calls) => new() { ToolCalls = calls.ToList() };
    private static string Join(Broker.NyaChat.Abstractions.NyaChatResult result) => string.Join("\n", result.Replies);

    private sealed class Fixture : IDisposable
    {
        public const string UserId = "ux-user";
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"nya_ux_{Guid.NewGuid():N}.db");
        private readonly ServiceProvider _services;
        public ScriptedProvider Provider { get; }
        public NyaMemoryService Memory => _services.GetRequiredService<NyaMemoryService>();
        public NyaAuditLogger Audit => _services.GetRequiredService<NyaAuditLogger>();

        public Fixture(IEnumerable<NyaLlmResponse?> responses, IEnumerable<Plugin>? plugins = null, string? deny = null, int maxReplyLength = 2000, int maxToolRounds = 3)
        {
            Provider = new ScriptedProvider(responses);
            var services = new ServiceCollection();
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddSingleton<INyaLlmProvider>(Provider);
            services.AddNyaChat(new NyaChatConfig
            {
                DbPath = $"{_path};Pooling=False",
                SummarizeTriggerCount = int.MaxValue,
                SummarizeKeepRecentCount = 1,
                MaxReplyLength = maxReplyLength,
                MaxToolRounds = maxToolRounds,
                LlmProfiles = new() { ["ux"] = new NyaLlmProfile { Provider = "ux", Model = "fake" } },
                TaskRouting = new() { ["chat"] = "ux", ["summarization"] = "ux", ["summarization"] = "ux" }
            });
            foreach (var plugin in plugins ?? Array.Empty<Plugin>()) services.AddSingleton<INyaToolPlugin>(plugin);
            if (deny != null) services.AddSingleton<INyaToolAuthorizer>(new DenyAuthorizer(deny));
            _services = services.BuildServiceProvider();
        }

        public Broker.NyaChat.Abstractions.NyaChatResult Chat(string message = "請協助我")
            => _services.GetRequiredService<INyaChatOrchestrator>().ChatAsync(new NyaChatRequest
            {
                ChannelType = "line", ChannelUserId = UserId, Message = message
            }, CancellationToken.None).GetAwaiter().GetResult();

        public List<NyaMessage> History(Broker.NyaChat.Abstractions.NyaChatResult result)
            => Memory.GetMessagesByTopic(NyaUserIdentity.Normalize("line", UserId), result.TopicId!);

        public void Summarize(string topicId)
            => _services.GetRequiredService<NyaSummarizer>().TrySummarizeAsync(UserId, topicId, force: true).GetAwaiter().GetResult();

        public void Dispose()
        {
            _services.Dispose();
            foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    private sealed class ScriptedProvider : INyaLlmProvider
    {
        private readonly Queue<NyaLlmResponse?> _responses;
        public List<List<NyaLlmMessage>> Messages { get; } = new();
        public List<int> ToolCounts { get; } = new();
        public string Key => "ux";
        public ScriptedProvider(IEnumerable<NyaLlmResponse?> responses) => _responses = new(responses);
        public Task<NyaLlmResponse?> SendAsync(NyaLlmProviderRequest request, CancellationToken ct)
        {
            Messages.Add(request.Request.Messages.Select(m => new NyaLlmMessage { Role = m.Role, Content = m.Content, Name = m.Name }).ToList());
            ToolCounts.Add(request.Request.Tools?.Count ?? 0);
            if (_responses.Count == 0) throw new InvalidOperationException("Unexpected LLM call");
            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class Plugin : INyaToolPlugin
    {
        public string Name { get; }
        private readonly NyaToolResult _result;
        private readonly bool _enabled;
        private readonly bool _throws;
        public int Executions;
        public Plugin(string name, NyaToolResult result, bool enabled = true, bool throws = false)
            => (Name, _result, _enabled, _throws) = (name, result, enabled, throws);
        public IReadOnlyList<NyaToolSchema> GetTools() => new[] { new NyaToolSchema { Name = Name, DefaultEnabled = _enabled } };
        public Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken ct)
        {
            Executions++;
            if (_throws) throw new InvalidOperationException("private details");
            return Task.FromResult(_result);
        }
    }

    private sealed class DenyAuthorizer : INyaToolAuthorizer
    {
        private readonly string _deny;
        public DenyAuthorizer(string deny) => _deny = deny;
        public bool CanUse(string userId, string channelType, NyaToolSchema tool) => tool.Name != _deny;
    }

    private static void AssertTrue(string name, bool condition)
    {
        if (condition) { Console.WriteLine($"  [PASS] {name}"); _passed++; }
        else { Console.Error.WriteLine($"  [FAIL] {name}"); _failed++; }
    }

    private static void AssertEqual<T>(string name, T expected, T actual)
    {
        if (Equals(expected, actual)) { Console.WriteLine($"  [PASS] {name}"); _passed++; }
        else { Console.Error.WriteLine($"  [FAIL] {name}: expected '{expected}', got '{actual}'"); _failed++; }
    }
}
