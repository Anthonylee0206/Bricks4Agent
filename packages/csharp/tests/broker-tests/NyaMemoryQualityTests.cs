using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

/// <summary>
/// 記憶品質：fact 調和（soft-retire / reactivate / reconciler 守衛）+ 摘要批次/模板。
/// %TEMP% 臨時 SQLite（Pooling=False + finally 清理；CLAUDE.md：nya_memq_*）。
/// </summary>
public static class NyaMemoryQualityTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0; _failed = 0;
        Console.WriteLine("=== NyaChat Memory Quality Tests (fact 調和 + 摘要豐富化) ===");
        Console.WriteLine();

        // Task 1: soft-retire
        TestRetireDeactivatesButKeepsHistory();
        TestReactivateRestoresRetiredFact();
        TestRetireNonexistentIsNoop();

        // Task 2: reconciler 解析 + 守衛
        TestReconcileParsesOps();
        TestReconcileProtectsInstructionFromRetire();
        TestReconcileDropsLowConfidenceRetire();
        TestReconcileCapsOpCount();
        TestReconcileSkipsMalformed();

        // Task 3: 套用調和操作
        TestApplyAddUpdateRetire();
        TestApplyRetireUsesSoftNotHardDelete();

        // Task 4: 抽取 prompt 改為調和語意
        TestExtractionPromptIsReconcile();

        // Task 5: 摘要模板納情感、去字數限
        TestSummaryTemplateKeepsEmotionNoCharCap();

        // Task 6: 小批次摘要
        TestPlanSummaryBatchCapsSize();

        // Task 7: 重複懲罰參數
        TestPenaltyParamsInCatalog();

        Console.WriteLine();
        Console.WriteLine($"=== Memory Quality Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    // ── Task 1: soft-retire ──────────────────────────────────────────────────────
    private static void TestRetireDeactivatesButKeepsHistory()
    {
        Console.WriteLine("--- retire: deactivates active fact but keeps it in history (reversible) ---");
        WithMem(mem =>
        {
            mem.UpsertFact("u1", "context", "exam_stress", "考試壓力大", null, 0.9f);
            var retired = mem.RetireFact("u1", "exam_stress", "考完了");

            AssertTrue("retire-returned", retired);
            AssertEqual("not-in-active", 0, mem.GetActiveFacts("u1").Count);
            AssertEqual("still-in-history", 1, mem.GetFactHistory("u1", "exam_stress").Count);
        });
    }

    private static void TestReactivateRestoresRetiredFact()
    {
        Console.WriteLine("--- reactivate: restores a retired fact to active ---");
        WithMem(mem =>
        {
            mem.UpsertFact("u1", "context", "exam_stress", "考試壓力大", null, 0.9f);
            mem.RetireFact("u1", "exam_stress", "考完了");
            var ok = mem.ReactivateFact("u1", "exam_stress");

            AssertTrue("reactivate-returned", ok);
            AssertEqual("back-in-active", 1, mem.GetActiveFacts("u1").Count);
        });
    }

    private static void TestRetireNonexistentIsNoop()
    {
        Console.WriteLine("--- retire: nonexistent key is a no-op (false) ---");
        WithMem(mem => AssertTrue("retire-false", !mem.RetireFact("u1", "nope", "x")));
    }

    // ── Task 2: reconciler 解析 + 守衛 ─────────────────────────────────────────────
    private static void TestReconcileParsesOps()
    {
        Console.WriteLine("--- reconcile: parses add/update/retire ops ---");
        var raw = "[{\"op\":\"add\",\"category\":\"preference\",\"key\":\"spicy\",\"value\":\"吃辣\",\"confidence\":0.9,\"reason\":\"明說\"}," +
                  "{\"op\":\"retire\",\"category\":\"context\",\"key\":\"exam\",\"confidence\":0.95,\"reason\":\"考完\"}]";
        var ops = NyaFactReconciler.Parse(raw, NyaFactReconciler.Defaults);
        AssertEqual("two-ops", 2, ops.Count);
        AssertEqual("first-add", "add", ops[0].Op);
        AssertEqual("second-retire", "retire", ops[1].Op);
    }

    private static void TestReconcileProtectsInstructionFromRetire()
    {
        Console.WriteLine("--- reconcile: retire on instruction category is dropped (protected) ---");
        var raw = "[{\"op\":\"retire\",\"category\":\"instruction\",\"key\":\"call_me_xm\",\"confidence\":0.99,\"reason\":\"x\"}]";
        var ops = NyaFactReconciler.Parse(raw, NyaFactReconciler.Defaults);
        AssertEqual("protected-dropped", 0, ops.Count);
    }

    private static void TestReconcileDropsLowConfidenceRetire()
    {
        // 0.7 介於 add 門檻(0.6) 與 retire 門檻(0.85) 之間：add 過、retire 不過 → 驗證不對稱門檻。
        // （plan 原稿用 0.5，但 0.5 < AddMinConfidence(0.6)，add 也會被丟，與「add kept」斷言矛盾。）
        Console.WriteLine("--- reconcile: mid-confidence retire is dropped, mid-confidence add kept (asymmetric floors) ---");
        var raw = "[{\"op\":\"retire\",\"category\":\"context\",\"key\":\"a\",\"confidence\":0.7,\"reason\":\"maybe\"}," +
                  "{\"op\":\"add\",\"category\":\"context\",\"key\":\"b\",\"value\":\"v\",\"confidence\":0.7,\"reason\":\"weak\"}]";
        var ops = NyaFactReconciler.Parse(raw, NyaFactReconciler.Defaults);
        AssertEqual("one-op", 1, ops.Count);
        AssertEqual("kept-add", "add", ops[0].Op);
    }

    private static void TestReconcileCapsOpCount()
    {
        Console.WriteLine("--- reconcile: caps total ops to MaxOps ---");
        var items = string.Join(",", Enumerable.Range(0, 50).Select(i =>
            $"{{\"op\":\"add\",\"category\":\"context\",\"key\":\"k{i}\",\"value\":\"v\",\"confidence\":0.9,\"reason\":\"r\"}}"));
        var ops = NyaFactReconciler.Parse("[" + items + "]", NyaFactReconciler.Defaults);
        AssertTrue("capped", ops.Count <= NyaFactReconciler.Defaults.MaxOps);
    }

    private static void TestReconcileSkipsMalformed()
    {
        Console.WriteLine("--- reconcile: malformed json / unknown op → skipped, no crash ---");
        AssertEqual("bad-json-empty", 0, NyaFactReconciler.Parse("not json", NyaFactReconciler.Defaults).Count);
        var raw = "[{\"op\":\"frobnicate\",\"key\":\"x\"},{\"op\":\"add\",\"category\":\"context\",\"key\":\"ok\",\"value\":\"v\",\"confidence\":0.9}]";
        var ops = NyaFactReconciler.Parse(raw, NyaFactReconciler.Defaults);
        AssertEqual("only-valid", 1, ops.Count);
        AssertEqual("valid-add", "add", ops[0].Op);
    }

    // ── Task 3: 套用調和操作 ───────────────────────────────────────────────────────
    private static void TestApplyAddUpdateRetire()
    {
        Console.WriteLine("--- apply: add inserts, update overwrites, retire deactivates ---");
        WithMem(mem =>
        {
            mem.UpsertFact("u1", "context", "old_task", "舊任務", null, 0.9f);
            var ops = new List<NyaFactOp>
            {
                new("add", "preference", "spicy", "吃辣", 0.9, "明說"),
                new("update", "context", "old_task", "新任務", 0.9, "改了"),
                new("retire", "context", "stale", "", 0.95, "過時"),   // stale 不存在 → retire no-op，不崩
            };
            mem.UpsertFact("u1", "context", "stale", "舊", null, 0.9f);
            ops.Add(new("retire", "context", "stale", "", 0.95, "過時"));

            var applied = NyaFactExtractor.ApplyReconcile(mem, "u1", ops, NullLogger<NyaFactExtractor>.Instance);

            var active = mem.GetActiveFacts("u1");
            AssertTrue("spicy-added", active.Any(f => f.FactKey == "spicy"));
            AssertTrue("task-updated", active.Any(f => f.FactKey == "old_task" && f.FactValue == "新任務"));
            AssertTrue("stale-retired", active.All(f => f.FactKey != "stale"));
            AssertTrue("some-applied", applied > 0);
        });
    }

    private static void TestApplyRetireUsesSoftNotHardDelete()
    {
        Console.WriteLine("--- apply: retire keeps history (soft), recoverable ---");
        WithMem(mem =>
        {
            mem.UpsertFact("u1", "context", "exam", "考試壓力", null, 0.9f);
            NyaFactExtractor.ApplyReconcile(mem, "u1",
                new List<NyaFactOp> { new("retire", "context", "exam", "", 0.95, "考完") },
                NullLogger<NyaFactExtractor>.Instance);

            AssertEqual("not-active", 0, mem.GetActiveFacts("u1").Count);
            AssertEqual("history-kept", 1, mem.GetFactHistory("u1", "exam").Count);
            AssertTrue("recoverable", mem.ReactivateFact("u1", "exam"));
        });
    }

    // ── Task 4: 抽取 prompt ────────────────────────────────────────────────────────
    private static void TestExtractionPromptIsReconcile()
    {
        Console.WriteLine("--- extraction prompt: asks for add/update/retire ops, not flat facts ---");
        var tpl = new NyaPromptTemplateProvider(new FixedConfigStore(new NyaChatConfig()),
            NullLogger<NyaPromptTemplateProvider>.Instance);
        var instr = tpl.Section("fact_extraction", "instruction");
        var fmt = tpl.Section("fact_extraction", "output_format");
        AssertContains("mentions-retire", instr + fmt, "retire");
        AssertContains("mentions-op", fmt, "\"op\"");
        // Phase 4：告誡 LLM 事實是全話題共用、長期持久 → 寧缺勿錯。
        AssertContains("warns-cross-topic", instr, "跨所有話題");
    }

    // ── Task 5: 摘要模板 ───────────────────────────────────────────────────────────
    private static void TestSummaryTemplateKeepsEmotionNoCharCap()
    {
        Console.WriteLine("--- summarization template: keeps emotion, drops 300-char cap, still excludes noise ---");
        var tpl = new NyaPromptTemplateProvider(new FixedConfigStore(new NyaChatConfig()),
            NullLogger<NyaPromptTemplateProvider>.Instance);
        var req = tpl.Section("summarization", "requirements");
        AssertContains("mentions-emotion", req, "情緒");
        AssertNotContains("no-300-cap", req, "300");
        AssertNotContains("no-exclude-roleplay", req, "角色扮演語氣");
        AssertContains("still-excludes-topic-ops", req, "話題");   // 仍排除話題管理操作
    }

    // ── Task 6: 小批次摘要 ─────────────────────────────────────────────────────────
    private static void TestPlanSummaryBatchCapsSize()
    {
        Console.WriteLine("--- PlanSummaryBatch: caps batch to maxBatch (small batches) ---");
        var msgs = Enumerable.Range(1, 40).Select(i => new NyaMessage
        { MessageId = "m" + i, UserId = "u1", Role = i % 2 == 0 ? "assistant" : "user",
          Content = "x", Sequence = i, IsSummarized = false }).ToList();

        // trigger=12, keepRecent=6, maxBatch=8 → 應只取 8 則（不是 40-6=34）
        var batch = NyaSummarizer.PlanSummaryBatch(msgs, triggerCount: 12, keepRecent: 6, force: false, maxBatch: 8);
        AssertTrue("batch-not-null", batch != null);
        AssertEqual("capped-to-8", 8, batch!.Count);
        AssertEqual("oldest-first", 1L, batch[0].Sequence);
    }

    // ── Task 7: 重複懲罰參數 ───────────────────────────────────────────────────────
    private static void TestPenaltyParamsInCatalog()
    {
        Console.WriteLine("--- param catalog: has repetition penalties for local/openai/ollama ---");
        var keys = Broker.NyaChat.NyaLlmParamCatalog.All.Select(p => p.Key).ToList();
        AssertTrue("freq-penalty", keys.Contains("frequency_penalty"));
        AssertTrue("presence-penalty", keys.Contains("presence_penalty"));
        AssertTrue("repeat-penalty", keys.Contains("repeat_penalty"));
    }

    // ── helpers ───────────────────────────────────────────────────────────────────
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
