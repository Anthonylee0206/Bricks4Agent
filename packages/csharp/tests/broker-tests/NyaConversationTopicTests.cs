using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

public static class NyaConversationTopicTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0; _failed = 0;
        Console.WriteLine("=== NyaChat Conversation Topic Tests ===");
        Console.WriteLine();
        TestUserGetsReply();
        TestRepeatedConversationMessages();
        TestUsersWithSharedTopicPrefix();
        Console.WriteLine();
        Console.WriteLine($"=== Conversation Topic Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static ServiceProvider Build(string nyaPath, FakeProvider llm)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton<INyaLlmProvider>(llm);
        services.AddNyaChat(new NyaChatConfig
        {
            Enabled = true,
            DbPath = $"{nyaPath};Pooling=False",
            LlmProfiles = new() { ["fake_chat"] = new NyaLlmProfile { Provider = "fake", Model = "m" } },
            TaskRouting = new()
            {
                ["chat"] = "fake_chat",
                ["summarization"] = "fake_chat",

            }
        });
        return services.BuildServiceProvider();
    }

    private static void TestUserGetsReply()
    {
        Console.WriteLine("--- any user admitted by the broker gets an LLM reply ---");
        var nyaPath = TempPath("nya_convtopic_reply");
        try
        {
            var llm = new FakeProvider();
            using var sp = Build(nyaPath, llm);
            var orch = sp.GetRequiredService<INyaChatOrchestrator>();
            var result = orch.ChatAsync(new NyaChatRequest
            {
                ChannelType = "line", ChannelUserId = "U_unbound", Message = "hi"
            }, CancellationToken.None).GetAwaiter().GetResult();

            AssertTrue("user-has-reply", result.Replies.Any(r => r.Contains("ok")));
            AssertTrue("user-llm-called", llm.CallCount > 0);
        }
        finally { Cleanup(nyaPath); }
    }

    private static void TestRepeatedConversationMessages()
    {
        Console.WriteLine("--- repeated messages honor conversationId ---");
        var nyaPath = TempPath("nya_convtopic_conversation");
        const string userId = "line-gen-conversation";
        const string topicId = "external-conversation";
        try
        {
            var llm = new FakeProvider();
            using var sp = Build(nyaPath, llm);
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

                AssertEqual($"conversation-topic-{i}", topicId, result.TopicId);
                AssertTrue($"conversation-no-error-{i}", result.Error == null);
                AssertTrue($"conversation-replies-{i}", result.Replies.Any(r => r.Contains("ok")));
                AssertEqual($"conversation-history-{i}", i * 2, result.HistoryCount);
            }

            var messages = memory.GetMessagesByTopic(userId, topicId);
            AssertEqual("conversation-message-count", 6, messages.Count);
            AssertTrue("conversation-user-messages", messages.Where(m => m.Role == "user")
                .Select(m => m.Content).SequenceEqual(new[] { "message 1", "message 2", "message 3" }));
            AssertTrue("conversation-sequences", messages.Select(m => m.Sequence)
                .SequenceEqual(Enumerable.Range(1, 6).Select(i => (long)i)));
            AssertEqual("conversation-active-topic-unchanged", activeTopic, memory.GetOrCreateActiveTopic(userId));
            AssertEqual("conversation-active-topic-empty", 0, memory.CountAllMessages(userId, activeTopic));
            AssertEqual("conversation-llm-calls", 3, llm.CallCount);
        }
        finally { Cleanup(nyaPath); }
    }

    private static void TestUsersWithSharedTopicPrefix()
    {
        Console.WriteLine("--- shared user prefix does not collide during topic creation ---");
        var nyaPath = TempPath("nya_convtopic_prefix");
        const string firstUser = "line-gen-first";
        const string secondUser = "line-gen-second";
        try
        {
            var llm = new FakeProvider();
            using var sp = Build(nyaPath, llm);
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

                AssertTrue($"prefix-topic-{i}", !string.IsNullOrEmpty(result.TopicId));
                AssertTrue($"prefix-no-error-{i}", result.Error == null);
                if (i == 1) secondTopic = result.TopicId;
                else AssertEqual($"prefix-reuses-topic-{i}", secondTopic, result.TopicId);
            }

            AssertTrue("prefix-topics-distinct", secondTopic != null && secondTopic != firstTopic);
            AssertEqual("prefix-message-count", 6, memory.CountAllMessages(secondUser, secondTopic));
            AssertEqual("prefix-single-topic", 1, memory.ListTopicsWithCount(secondUser).Count);
            AssertEqual("prefix-first-user-topic-unchanged", firstTopic, memory.GetOrCreateActiveTopic(firstUser));
            AssertEqual("prefix-first-user-messages-empty", 0, memory.CountAllMessages(firstUser));
            AssertEqual("prefix-llm-calls", 3, llm.CallCount);
        }
        finally { Cleanup(nyaPath); }
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
                try { if (File.Exists(p)) File.Delete(p); } catch { }
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
