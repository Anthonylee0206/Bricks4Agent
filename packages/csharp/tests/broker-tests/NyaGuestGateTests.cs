using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

/// <summary>
/// Guest 身分組（2026-07-08 spec）：無 principal 綁定的來源視為 guest——
/// 訊息落 nya_messages、audit 記 guest_message_blocked、不呼叫 LLM、Replies 為空。
/// 有綁定者照常走 LLM。使用 %TEMP% 臨時 SQLite（Pooling=False + finally 清理）。
/// </summary>
public static class NyaGuestGateTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0; _failed = 0;
        Console.WriteLine("=== NyaChat Guest Gate Tests (白名單外靜默記錄) ===");
        Console.WriteLine();
        TestGuestIsLoggedButNotAnswered();
        TestAuthorizedUserGetsReply();
        TestRepeatedConversationMessages(authorized: false);
        TestRepeatedConversationMessages(authorized: true);
        TestUsersWithSharedTopicPrefix(authorized: false);
        TestUsersWithSharedTopicPrefix(authorized: true);
        Console.WriteLine();
        Console.WriteLine($"=== Guest Gate Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static ServiceProvider Build(string nyaPath, FakeProvider llm, INyaPrincipalResolver resolver)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        // 全圖組裝需要 IConfiguration（NyaToolEnableStore）；空配置即可。
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton<INyaLlmProvider>(llm);
        services.AddSingleton(resolver);
        services.AddNyaChat(new NyaChatConfig
        {
            Enabled = true,
            DbPath = $"{nyaPath};Pooling=False",
            LlmProfiles = new() { ["fake_chat"] = new NyaLlmProfile { Provider = "fake", Model = "m" } },
            TaskRouting = new()
            {
                ["chat"] = "fake_chat",
                ["fact_extraction"] = "fake_chat",
                ["summarization"] = "fake_chat"
            }
        });
        return services.BuildServiceProvider();
    }

    private static void TestGuestIsLoggedButNotAnswered()
    {
        Console.WriteLine("--- guest: message persisted + audit, no LLM call, empty replies ---");
        var nyaPath = TempPath("nya_guestgate");
        try
        {
            var llm = new FakeProvider();
            using (var sp = Build(nyaPath, llm, new FakeResolver(allowed: "U_member")))
            {
                var orch = sp.GetRequiredService<INyaChatOrchestrator>();
                var result = orch.ChatAsync(new NyaChatRequest
                {
                    ChannelType = "line", ChannelUserId = "U_guest", Message = "hello?"
                }, CancellationToken.None).GetAwaiter().GetResult();

                AssertTrue("guest-empty-replies", result.Replies.All(string.IsNullOrEmpty));
                AssertEqual("guest-no-llm-call", 0, llm.CallCount);
            }
            using var verify = BrokerDb.UseSqlite($"Data Source={nyaPath};Pooling=False");
            AssertEqual("guest-message-row", 1,
                verify.Scalar<int>("SELECT COUNT(*) FROM nya_messages WHERE user_id = 'U_guest' AND role = 'user'", null));
            AssertEqual("guest-audit-row", 1,
                verify.Scalar<int>("SELECT COUNT(*) FROM nya_audit_log WHERE user_id = 'U_guest' AND action = 'guest_message_blocked'", null));
        }
        finally { Cleanup(nyaPath); }
    }

    private static void TestAuthorizedUserGetsReply()
    {
        Console.WriteLine("--- bound user: normal LLM reply ---");
        var nyaPath = TempPath("nya_guestgate_ok");
        try
        {
            var llm = new FakeProvider();
            using var sp = Build(nyaPath, llm, new FakeResolver(allowed: "U_member"));
            var orch = sp.GetRequiredService<INyaChatOrchestrator>();
            var result = orch.ChatAsync(new NyaChatRequest
            {
                ChannelType = "line", ChannelUserId = "U_member", Message = "hi"
            }, CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue("member-has-reply", result.Replies.Any(r => r.Contains("ok")));
            AssertTrue("member-llm-called", llm.CallCount > 0);
        }
        finally { Cleanup(nyaPath); }
    }

    private static void TestRepeatedConversationMessages(bool authorized)
    {
        var label = authorized ? "member" : "guest";
        Console.WriteLine($"--- {label}: repeated messages honor conversationId ---");
        var nyaPath = TempPath("nya_guestgate_conversation");
        const string userId = "line-gen-conversation";
        const string topicId = "external-conversation";
        try
        {
            var llm = new FakeProvider();
            using var sp = Build(nyaPath, llm, new FakeResolver(authorized ? userId : "U_member"));
            var orch = sp.GetRequiredService<INyaChatOrchestrator>();
            var memory = sp.GetRequiredService<NyaMemoryService>();
            var activeTopic = memory.GetOrCreateActiveTopic(userId);
            for (var i = 1; i <= 3; i++)
            {
                var result = orch.ChatAsync(new NyaChatRequest
                {
                    ChannelType = "line", ChannelUserId = userId,
                    ConversationId = topicId, Message = $"message {i}"
                }, CancellationToken.None).GetAwaiter().GetResult();

                AssertEqual($"{label}-conversation-topic-{i}", topicId, result.TopicId);
                AssertTrue($"{label}-conversation-no-error-{i}", result.Error == null);
                AssertTrue($"{label}-conversation-replies-{i}", authorized
                    ? result.Replies.Any(r => r.Contains("ok"))
                    : result.Replies.Count == 0);
                AssertEqual($"{label}-conversation-history-{i}", authorized ? i * 2 : 0, result.HistoryCount);
            }

            var messages = memory.GetMessagesByTopic(userId, topicId);
            AssertEqual($"{label}-conversation-message-count", authorized ? 6 : 3, messages.Count);
            AssertTrue($"{label}-conversation-user-messages", messages.Where(m => m.Role == "user")
                .Select(m => m.Content).SequenceEqual(new[] { "message 1", "message 2", "message 3" }));
            AssertTrue($"{label}-conversation-sequences", messages.Select(m => m.Sequence)
                .SequenceEqual(Enumerable.Range(1, authorized ? 6 : 3).Select(i => (long)i)));
            AssertEqual($"{label}-conversation-active-topic-unchanged", activeTopic, memory.GetOrCreateActiveTopic(userId));
            AssertEqual($"{label}-conversation-active-topic-empty", 0, memory.CountAllMessages(userId, activeTopic));
            AssertEqual($"{label}-conversation-llm-calls", authorized ? 3 : 0, llm.CallCount);
            var db = sp.GetRequiredService<NyaDb>().Db;
            AssertEqual($"{label}-conversation-audit-count", 3, db.Scalar<int>(
                "SELECT COUNT(*) FROM nya_audit_log WHERE user_id = @userId AND action = @action",
                new { userId, action = authorized ? "chat_output" : "guest_message_blocked" }));
        }
        finally { Cleanup(nyaPath); }
    }

    private static void TestUsersWithSharedTopicPrefix(bool authorized)
    {
        var label = authorized ? "member" : "guest";
        Console.WriteLine($"--- {label}: shared user prefix does not collide during topic creation ---");
        var nyaPath = TempPath("nya_guestgate_prefix");
        const string firstUser = "line-gen-first";
        const string secondUser = "line-gen-second";
        try
        {
            var llm = new FakeProvider();
            using var sp = Build(nyaPath, llm, new FakeResolver(authorized ? secondUser : "U_member"));
            var memory = sp.GetRequiredService<NyaMemoryService>();
            var firstTopic = memory.GetOrCreateActiveTopic(firstUser);
            var orch = sp.GetRequiredService<INyaChatOrchestrator>();
            string? secondTopic = null;
            for (var i = 1; i <= 3; i++)
            {
                var result = orch.ChatAsync(new NyaChatRequest
                {
                    ChannelType = "line", ChannelUserId = secondUser, Message = $"message {i}",
                    ConversationId = i == 1 ? null : secondTopic
                }, CancellationToken.None).GetAwaiter().GetResult();

                AssertTrue($"{label}-prefix-topic-{i}", !string.IsNullOrEmpty(result.TopicId));
                AssertTrue($"{label}-prefix-no-error-{i}", result.Error == null);
                if (i == 1) secondTopic = result.TopicId;
                else AssertEqual($"{label}-prefix-reuses-topic-{i}", secondTopic, result.TopicId);
            }

            AssertTrue($"{label}-prefix-topics-distinct", secondTopic != null && secondTopic != firstTopic);
            AssertEqual($"{label}-prefix-message-count", authorized ? 6 : 3, memory.CountAllMessages(secondUser, secondTopic));
            AssertEqual($"{label}-prefix-single-topic", 1, memory.ListTopicsWithCount(secondUser).Count);
            AssertEqual($"{label}-prefix-first-user-topic-unchanged", firstTopic, memory.GetOrCreateActiveTopic(firstUser));
            AssertEqual($"{label}-prefix-first-user-messages-empty", 0, memory.CountAllMessages(firstUser));
            AssertEqual($"{label}-prefix-llm-calls", authorized ? 3 : 0, llm.CallCount);
        }
        finally { Cleanup(nyaPath); }
    }

    // ── fakes & helpers ──────────────────────────────────────────────────────

    private sealed class FakeResolver : INyaPrincipalResolver
    {
        private readonly string _allowed;
        public FakeResolver(string allowed) { _allowed = allowed; }
        public bool IsAuthorized(string userId, string channelType) => userId == _allowed;
    }

    private sealed class FakeProvider : INyaLlmProvider
    {
        public string Key => "fake";
        public bool SupportsTools => true;
        public int CallCount;
        public Task<NyaLlmResponse?> SendAsync(NyaLlmProviderRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult<NyaLlmResponse?>(new NyaLlmResponse { Content = "ok" });
        }
    }

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
