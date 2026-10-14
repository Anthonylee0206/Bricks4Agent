using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

public static class NyaMemoryQualityTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0; _failed = 0;
        Console.WriteLine("=== NyaChat Memory Quality Tests (fact 調和 + 摘要豐富化) ===");
        Console.WriteLine();

        TestSummaryTemplateKeepsEmotionNoCharCap();

        TestPlanSummaryBatchCapsSize();

        TestPenaltyParamsInCatalog();

        Console.WriteLine();
        Console.WriteLine($"=== Memory Quality Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static void TestSummaryTemplateKeepsEmotionNoCharCap()
    {
        Console.WriteLine("--- summarization template: keeps emotion, drops 300-char cap, still excludes noise ---");
        var tpl = new NyaPromptTemplateProvider(new FixedConfigStore(new NyaChatConfig()),
            NullLogger<NyaPromptTemplateProvider>.Instance);
        var req = tpl.Section("summarization", "requirements");
        AssertContains("mentions-emotion", req, "情緒");
        AssertNotContains("no-300-cap", req, "300");
        AssertNotContains("no-exclude-roleplay", req, "角色扮演語氣");
        AssertContains("still-excludes-topic-ops", req, "話題");
    }

    private static void TestPlanSummaryBatchCapsSize()
    {
        Console.WriteLine("--- PlanSummaryBatch: caps batch to maxBatch (small batches) ---");
        var msgs = Enumerable.Range(1, 40).Select(i => new NyaMessage
        { MessageId = "m" + i, UserId = "u1", Role = i % 2 == 0 ? "assistant" : "user",
          Content = "x", Sequence = i, IsSummarized = false }).ToList();

        var batch = NyaSummarizer.PlanSummaryBatch(msgs, triggerCount: 12, keepRecent: 6, force: false, maxBatch: 8);
        AssertTrue("batch-not-null", batch != null);
        AssertEqual("capped-to-8", 8, batch!.Count);
        AssertEqual("oldest-first", 1L, batch[0].Sequence);
    }

    private static void TestPenaltyParamsInCatalog()
    {
        Console.WriteLine("--- param catalog: has repetition penalties for local/openai/ollama ---");
        var keys = Broker.NyaChat.NyaLlmParamCatalog.All.Select(p => p.Key).ToList();
        AssertTrue("freq-penalty", keys.Contains("frequency_penalty"));
        AssertTrue("presence-penalty", keys.Contains("presence_penalty"));
        AssertTrue("repeat-penalty", keys.Contains("repeat_penalty"));
    }

    private static void WithMem(Action<NyaMemoryService> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nya_memq_{Guid.NewGuid():N}.db");
        var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        try
        {
            var cfg = new FixedConfigStore(new NyaChatConfig());
            var mem = new NyaMemoryService(db, cfg, NullLogger<NyaMemoryService>.Instance);
            body(mem);
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
        }
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
    private static void AssertContains(string name, string actual, string expected)
    {
        if (actual.Contains(expected)) { Console.WriteLine($"  [PASS] {name}"); _passed++; }
        else { Console.Error.WriteLine($"  [FAIL] {name}: expected to contain '{expected}', got '{actual}'"); _failed++; }
    }
    private static void AssertNotContains(string name, string actual, string unexpected)
    {
        if (!actual.Contains(unexpected)) { Console.WriteLine($"  [PASS] {name}"); _passed++; }
        else { Console.Error.WriteLine($"  [FAIL] {name}: expected NOT to contain '{unexpected}'"); _failed++; }
    }
}
