using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

/// <summary>
/// 話題隔離與話題工具測試（大項#3 對話/摘要依話題獨立、#5 topicID 格式、#7 LLM 話題工具、#6 稽核關鍵字）。
/// 使用 %TEMP% 臨時 SQLite（Pooling=False + finally 清理，遵循 CLAUDE.md 規約）。
/// </summary>
public static class NyaTopicTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0;
        _failed = 0;
        Console.WriteLine("=== NyaChat Topic Isolation & Tools Tests (#3/#5/#7/#6) ===");
        Console.WriteLine();

        WithDb(TestMessagesIsolatedByTopic);
        WithDb(TestSummariesIsolatedByTopic);
        WithDb(TestLegacyNullTopicSummaryDoesNotLeak);
        WithDb(TestTopicIdFormat);
        WithDb(TestTopicToolNewSwitchRename);
        WithDb(TestAuditKeywordSearch);
        WithDb(TestDeleteMessagesByIds);
        WithDb(TestListUsersKeepsUsersWithoutActiveMessages);
        WithDb(TestMessagesTopicFilter);
        WithDb(TestCountActiveMessagesByTopic);
        WithDb(TestDeleteTopicReassignsActive);
        WithDb(TestAuditDelete);
        WithDb(TestAuditDeleteByFilter);
        WithDb(TestSummaryAuditRows);
        TestSummaryPlanForceVsAuto();

        Console.WriteLine();
        Console.WriteLine($"=== Topic Tests Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    // ── #3：對話依話題獨立 ────────────────────────────────────────────────────────
    private static void TestMessagesIsolatedByTopic(NyaMemoryService mem, NyaAuditLogger _, BrokerDb __)
    {
        Console.WriteLine("--- recent messages isolated by topic (#3) ---");
        const string u = "u_iso";
        mem.AppendMessage(u, "user", "hello in A", "topicA");
        mem.AppendMessage(u, "assistant", "reply in A", "topicA");
        mem.AppendMessage(u, "user", "hello in B", "topicB");

        var a = mem.GetRecentMessages(u, "topicA", 30);
        var b = mem.GetRecentMessages(u, "topicB", 30);
        AssertTrue("topicA-count", a.Count == 2);
        AssertTrue("topicB-count", b.Count == 1);
        AssertTrue("topicA-no-bleed", a.All(m => m.Content.Contains("in A")));
        AssertTrue("topicB-no-bleed", b.All(m => m.Content.Contains("in B")));
    }

    // ── #3：摘要依話題獨立 ────────────────────────────────────────────────────────
    private static void TestSummariesIsolatedByTopic(NyaMemoryService mem, NyaAuditLogger _, BrokerDb __)
    {
        Console.WriteLine("--- summaries isolated by topic; cross-topic create does not retire (#3) ---");
        const string u = "u_sum";
        var sA = mem.CreateSummary(u, "topicA", "summary of A", 1, 5, 5);
        AssertTrue("A-active-after-create", mem.GetActiveSummaries(u, "topicA").Any(s => s.SummaryId == sA.SummaryId));
        AssertTrue("B-empty-before", mem.GetActiveSummaries(u, "topicB").Count == 0);

        // 在 B 話題建立摘要，不得退役 A 的 active 摘要
        var sB = mem.CreateSummary(u, "topicB", "summary of B", 1, 5, 5);
        AssertTrue("A-still-active", mem.GetActiveSummaries(u, "topicA").Any(s => s.SummaryId == sA.SummaryId));
        AssertTrue("B-active", mem.GetActiveSummaries(u, "topicB").Any(s => s.SummaryId == sB.SummaryId));
        AssertTrue("A-does-not-see-B", mem.GetActiveSummaries(u, "topicA").All(s => s.SummaryId != sB.SummaryId));
    }

    private static void TestLegacyNullTopicSummaryDoesNotLeak(NyaMemoryService mem, NyaAuditLogger _, BrokerDb db)
    {
        Console.WriteLine("--- legacy NULL-topic summary does NOT leak into a topic (Fix 3 isolation) ---");
        const string u = "u_legacy";
        // 直接寫入舊版（topic_id = null）摘要
        var legacy = new NyaSummary
        {
            SummaryId = "nyas_legacy_1",
            UserId = u,
            TopicId = null,
            SummaryText = "legacy global summary",
            CoveredFromSeq = 1, CoveredToSeq = 2, MessageCount = 2,
            Version = 1, IsActive = true, CreatedAt = DateTime.UtcNow
        };
        db.Insert(legacy);

        // Fix 3：per-topic 取摘要不得洩入 topic_id IS NULL 的全域/舊摘要，
        // 否則單一全域摘要會出現在每個話題的 context 與合併輸入中（跨話題汙染）。
        var got = mem.GetActiveSummaries(u, "any_topic");
        AssertTrue("legacy-does-not-leak", got.All(s => s.SummaryId != legacy.SummaryId));
    }

    // ── #5：topicID 格式 ──────────────────────────────────────────────────────────
    private static void TestTopicIdFormat(NyaMemoryService mem, NyaAuditLogger _, BrokerDb __)
    {
        Console.WriteLine("--- topicID format {prefix}_topic_{n} (#5) ---");
        const string u = "lineU123456789";
        var t1 = mem.CreateNewTopic(u, null);
        var t2 = mem.CreateNewTopic(u, "second");
        AssertTrue("t1-format", t1.TopicId == "lineU12_topic_1", t1.TopicId);
        AssertTrue("t2-format", t2.TopicId == "lineU12_topic_2", t2.TopicId);

        // 前綴非英數字元以 '_' 取代
        var t3 = mem.CreateNewTopic("ab:cd!ef", null);
        AssertTrue("t3-sanitized-prefix", t3.TopicId.StartsWith("ab_cd_e_topic_"), t3.TopicId);
    }

    // ── #7：LLM 話題工具 ──────────────────────────────────────────────────────────
    private static void TestTopicToolNewSwitchRename(NyaMemoryService mem, NyaAuditLogger audit, BrokerDb __)
    {
        Console.WriteLine("--- topic tools new/switch/rename execute + audit (#7) ---");
        const string u = "u_tool";
        var plugin = new TopicToolPlugin(mem, audit, NullLogger<TopicToolPlugin>.Instance);

        // new_topic
        var rNew = Exec(plugin, "new_topic", u, currentTopic: null, args: "{\"title\":\"Trips\"}");
        AssertTrue("new-success", rNew.Success && rNew.LlmContent.Contains("\"success\":true"));

        var active = mem.ListTopicsWithCount(u).FirstOrDefault(t => t.IsActive);
        AssertTrue("new-active-title", active != null && active.Title == "Trips");

        // 另開一個話題，再用標題切回 Trips
        mem.CreateNewTopic(u, "Work");
        var rSwitch = Exec(plugin, "switch_topic", u, currentTopic: null, args: "{\"topic\":\"Trips\"}");
        AssertTrue("switch-success", rSwitch.Success && rSwitch.LlmContent.Contains("\"success\":true"));
        var nowActive = mem.ListTopicsWithCount(u).FirstOrDefault(t => t.IsActive);
        AssertTrue("switch-by-title", nowActive != null && nowActive.Title == "Trips");

        // switch 不存在
        var rMiss = Exec(plugin, "switch_topic", u, currentTopic: null, args: "{\"topic\":\"NoSuch\"}");
        AssertTrue("switch-missing-fail", !rMiss.Success && rMiss.LlmContent.Contains("topic_not_found"));

        // rename 目前話題（current = active Trips）
        var renameCurrent = nowActive!.TopicId;
        var rRename = Exec(plugin, "rename_topic", u, currentTopic: renameCurrent, args: "{\"title\":\"Vacation\"}");
        AssertTrue("rename-success", rRename.Success && rRename.LlmContent.Contains("Vacation"));
        var renamed = mem.ListTopicsWithCount(u).FirstOrDefault(t => t.TopicId == renameCurrent);
        AssertTrue("rename-applied", renamed != null && renamed.Title == "Vacation");

        // 審計記錄了 topic_rename
        var (logs, total) = audit.GetLogs(u, "topic_rename");
        AssertTrue("rename-audited", total >= 1 && logs.Any(l => l.Action == "topic_rename"));
    }

    // ── #6：稽核關鍵字搜尋 ────────────────────────────────────────────────────────
    private static void TestAuditKeywordSearch(NyaMemoryService _, NyaAuditLogger audit, BrokerDb __)
    {
        Console.WriteLine("--- audit keyword search over detail_json (#6) ---");
        const string u = "u_audit_kw";
        audit.LogTopicRename(u, "t1", "ZzUniqueTitle");
        audit.LogTopicCreate(u, "t2", "Other");

        var (hit, hitTotal) = audit.GetLogs(u, keyword: "ZzUniqueTitle");
        AssertTrue("keyword-hits", hitTotal >= 1 && hit.All(l => l.DetailJson.Contains("ZzUniqueTitle")));

        var (miss, missTotal) = audit.GetLogs(u, keyword: "NothingMatchesThis");
        AssertTrue("keyword-miss-empty", missTotal == 0 && miss.Count == 0);
    }

    // ── #4：選取軟刪除（BaseOrm IN 展開 bug）─────────────────────────────────────
    private static void TestDeleteMessagesByIds(NyaMemoryService mem, NyaAuditLogger _, BrokerDb __)
    {
        Console.WriteLine("--- DeleteMessagesByIds soft-deletes only the given ids (#4) ---");
        const string u = "u_delids";
        var m1 = mem.AppendMessage(u, "user", "one", "t");
        var m2 = mem.AppendMessage(u, "user", "two", "t");
        var m3 = mem.AppendMessage(u, "user", "three", "t");

        var n = mem.DeleteMessagesByIds(u, new[] { m1.MessageId, m3.MessageId });
        AssertTrue("deleted-count-2", n == 2, n.ToString());

        var remaining = mem.GetAllMessages(u, 50, 0);
        AssertTrue("only-m2-remains", remaining.Count == 1 && remaining[0].MessageId == m2.MessageId);
    }

    private static void TestListUsersKeepsUsersWithoutActiveMessages(NyaMemoryService mem, NyaAuditLogger _, BrokerDb __)
    {
        Console.WriteLine("--- ListUsers keeps users whose messages are all deleted or who only have facts ---");
        mem.AppendMessage("u_live", "user", "hi", "t");
        var gone = mem.AppendMessage("u_gone", "user", "bye", "t");
        mem.DeleteMessagesByIds("u_gone", new[] { gone.MessageId });
        mem.UpsertFact("u_factonly", "identity", "job", "engineer");

        var (users, total) = mem.ListUsers();
        AssertTrue("total-3", total == 3, total.ToString());
        AssertTrue("live-first", users.Count == 3 && users[0].UserId == "u_live" && users[0].MsgCount == 1);
        AssertTrue("gone-listed-zero", users.Any(x => x.UserId == "u_gone" && x.MsgCount == 0));
        AssertTrue("factonly-listed", users.Any(x => x.UserId == "u_factonly" && x.ActiveFactCount == 1));

        var (filtered, filteredTotal) = mem.ListUsers(keyword: "gone");
        AssertTrue("keyword-filter", filteredTotal == 1 && filtered.Single().UserId == "u_gone");
    }

    // ── #5：訊息依話題篩選 ────────────────────────────────────────────────────────
    private static void TestMessagesTopicFilter(NyaMemoryService mem, NyaAuditLogger _, BrokerDb __)
    {
        Console.WriteLine("--- GetAllMessages/CountAllMessages topic filter (#5) ---");
        const string u = "u_msgfilter";
        mem.AppendMessage(u, "user", "a1", "tA");
        mem.AppendMessage(u, "user", "a2", "tA");
        mem.AppendMessage(u, "user", "b1", "tB");

        AssertTrue("filter-tA", mem.GetAllMessages(u, 50, 0, "tA").Count == 2);
        AssertTrue("filter-tB", mem.GetAllMessages(u, 50, 0, "tB").Count == 1);
        AssertTrue("filter-none-all", mem.GetAllMessages(u, 50, 0, null).Count == 3);
        AssertTrue("count-tA", mem.CountAllMessages(u, "tA") == 2);
        AssertTrue("count-all", mem.CountAllMessages(u, null) == 3);
    }

    // ── Fix 2：per-topic 未摘要(active)訊息計數（觸發透明度）─────────────────────
    private static void TestCountActiveMessagesByTopic(NyaMemoryService mem, NyaAuditLogger _, BrokerDb __)
    {
        Console.WriteLine("--- CountActiveMessages counts only unsummarized, per topic (Fix 2) ---");
        const string u = "u_active";
        mem.AppendMessage(u, "user", "a1", "tA");
        mem.AppendMessage(u, "user", "a2", "tA");
        mem.AppendMessage(u, "user", "a3", "tA");
        mem.AppendMessage(u, "user", "b1", "tB");

        // 標記 tA 最舊 1 則為已摘要 → 不計入 active
        var aMsgs = mem.GetMessagesByTopic(u, "tA");
        mem.MarkAsSummarized(u, "tA", aMsgs.First().Sequence, aMsgs.First().Sequence);

        AssertTrue("active-tA-2", mem.CountActiveMessages(u, "tA") == 2, mem.CountActiveMessages(u, "tA").ToString());
        AssertTrue("active-tB-1", mem.CountActiveMessages(u, "tB") == 1);
        AssertTrue("active-empty-0", mem.CountActiveMessages(u, "tEmpty") == 0);
    }

    // ── #2：刪除話題（含 active 重指派）──────────────────────────────────────────
    private static void TestDeleteTopicReassignsActive(NyaMemoryService mem, NyaAuditLogger _, BrokerDb __)
    {
        Console.WriteLine("--- DeleteTopic cascades + reassigns active (#2) ---");
        const string u = "u_deltopic";
        mem.CreateNewTopic(u, "A");
        var tB = mem.CreateNewTopic(u, "B");
        var tC = mem.CreateNewTopic(u, "C"); // active
        mem.AppendMessage(u, "user", "inC", tC.TopicId);
        mem.CreateSummary(u, tC.TopicId, "sumC", 1, 1, 1);

        var ok = mem.DeleteTopic(u, tC.TopicId);
        AssertTrue("delete-active-ok", ok);

        var topics = mem.ListTopicsWithCount(u);
        AssertTrue("tC-gone", topics.All(t => t.TopicId != tC.TopicId));
        AssertTrue("reassigned-active-tB", topics.FirstOrDefault(t => t.IsActive)?.TopicId == tB.TopicId, "active=" + topics.FirstOrDefault(t => t.IsActive)?.TopicId);
        AssertTrue("tC-msgs-softdeleted", mem.GetAllMessages(u, 50, 0, tC.TopicId).Count == 0);
        AssertTrue("tC-summary-gone", mem.GetSummaryHistory(u).All(s => s.TopicId != tC.TopicId));
    }

    // ── #3：稽核日誌刪除 ──────────────────────────────────────────────────────────
    private static void TestAuditDelete(NyaMemoryService _, NyaAuditLogger audit, BrokerDb __)
    {
        Console.WriteLine("--- audit delete one / clear user (#3) ---");
        const string u = "u_auditdel";
        audit.LogTopicCreate(u, "t1", "A");
        audit.LogTopicCreate(u, "t2", "B");

        var (logs, total) = audit.GetLogs(u);
        AssertTrue("two-logs", total == 2);

        AssertTrue("del-one", audit.DeleteLog(logs[0].LogId) == 1);
        AssertTrue("one-left", audit.GetLogs(u).Total == 1);
        AssertTrue("clear-user", audit.DeleteLogs(u) >= 1);
        AssertTrue("empty-after-clear", audit.GetLogs(u).Total == 0);
    }

    // ── 稽核依篩選清空：刪除範圍 = 同條件下列表的筆數 ────────────────────────────────
    private static void TestAuditDeleteByFilter(NyaMemoryService _, NyaAuditLogger audit, BrokerDb __)
    {
        Console.WriteLine("--- audit clear follows filter: deletes exactly what the same filter lists ---");
        const string u1 = "u_filt1", u2 = "u_filt2";
        audit.LogTopicCreate(u1, "t1", "A");
        audit.LogTopicSwitch(u1, "t1", "t2");
        audit.LogTopicCreate(u2, "t3", "B");

        var listed = audit.GetLogs(action: "topic_create").Total;
        AssertTrue("action-filter-deletes-listed", listed == 2 && audit.DeleteLogs(action: "topic_create") == listed);
        AssertTrue("other-action-kept", audit.GetLogs(u1).Total == 1);
        AssertTrue("user-filter", audit.DeleteLogs(u1) == 1 && audit.GetLogs().Total == 0);
    }

    // ── 摘要稽核留痕（Root cause A：成功含 topic_id；空內容 skipped 可觀測）───────────
    private static void TestSummaryAuditRows(NyaMemoryService _, NyaAuditLogger audit, BrokerDb __)
    {
        Console.WriteLine("--- summary audit: creation carries topic_id; empty content logs skipped ---");
        const string u = "u_sumaudit";

        // 成功建立 → summary_create / success，detail 含 topic_id
        audit.LogSummaryCreation(u, "topicX", "nyas_x", 1, 10, 10, "qwen-test");
        // LLM 回空 → summary_create / skipped，detail 含 reason + topic_id
        audit.LogSummarySkipped(u, "topicY", "llm_empty");

        var (logs, total) = audit.GetLogs(u, "summary_create");
        AssertTrue("two-summary-rows", total == 2, total.ToString());

        var created = logs.FirstOrDefault(l => l.Result == "success");
        AssertTrue("create-success-has-topic",
            created != null && created.DetailJson.Contains("topicX") && created.DetailJson.Contains("nyas_x"),
            created?.DetailJson);

        var skipped = logs.FirstOrDefault(l => l.Result == "skipped");
        AssertTrue("skipped-has-reason-and-topic",
            skipped != null && skipped.DetailJson.Contains("llm_empty") && skipped.DetailJson.Contains("topicY"),
            skipped?.DetailJson);
    }

    // ── 立即摘要修復：手動 force 略過自動門檻（純函式）────────────────────────────
    private static void TestSummaryPlanForceVsAuto()
    {
        Console.WriteLine("--- PlanSummaryBatch: manual force bypasses auto trigger gate (立即摘要 fix) ---");
        const int trigger = 30, keep = 20;

        // 25 則 < 30：自動路徑不摘要，手動 force 應摘要最舊 5 則
        AssertTrue("auto-gated-below-trigger",
            NyaSummarizer.PlanSummaryBatch(Msgs(25), trigger, keep, force: false) == null);
        var forced = NyaSummarizer.PlanSummaryBatch(Msgs(25), trigger, keep, force: true);
        AssertTrue("force-summarizes-5", forced != null && forced.Count == 5 && forced[0].Sequence == 1 && forced[^1].Sequence == 5,
            forced == null ? "null" : forced.Count.ToString());

        // 35 則 ≥ 30：自動路徑也會摘要最舊 15 則
        var auto = NyaSummarizer.PlanSummaryBatch(Msgs(35), trigger, keep, force: false);
        AssertTrue("auto-fires-15", auto != null && auto.Count == 15);

        // 太短（≤ keep）：即使 force 也無可壓縮 → null
        AssertTrue("force-too-small-null",
            NyaSummarizer.PlanSummaryBatch(Msgs(15), trigger, keep, force: true) == null);

        // 已摘要的不計入：30 則但前 12 已摘要 → active 18 ≤ keep 20 → null
        AssertTrue("excludes-summarized",
            NyaSummarizer.PlanSummaryBatch(Msgs(30, 12), trigger, keep, force: true) == null);

        // ── 摺疊遲滯（Root cause B）：門檻看 active 數，非總數 ──────────────────────────
        // 40 則但前 25 已摘要 → active 15 < trigger 30：摺疊後尚未累積足夠新訊息，不得重觸發。
        // （舊行為用總數 40 ≥ 30 會每輪重觸發 → 沒摺疊。）
        AssertTrue("hysteresis-active-below-trigger-null",
            NyaSummarizer.PlanSummaryBatch(Msgs(40, 25), trigger, keep, force: false) == null);

        // 40 則前 5 已摘要 → active 35 ≥ 30：累積足夠 → 摺疊最舊 15 則（35 - keep 20）。
        var resumed = NyaSummarizer.PlanSummaryBatch(Msgs(40, 5), trigger, keep, force: false);
        AssertTrue("hysteresis-active-at-trigger-fires-15",
            resumed != null && resumed.Count == 15 && resumed.All(m => m.Sequence > 5),
            resumed == null ? "null" : resumed.Count.ToString());
    }

    private static List<NyaMessage> Msgs(int n, int summarizedPrefix = 0)
        => Enumerable.Range(1, n)
            .Select(i => new NyaMessage { Sequence = i, IsSummarized = i <= summarizedPrefix })
            .ToList();

    // ── 共用 ──────────────────────────────────────────────────────────────────────
    private static NyaToolResult Exec(TopicToolPlugin plugin, string tool, string userId, string? currentTopic, string args)
        => plugin.ExecuteAsync(new NyaToolContext
        {
            ToolName = tool,
            UserId = userId,
            ChannelType = "web",
            ConversationId = currentTopic,
            ArgumentsJson = args
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static void WithDb(Action<NyaMemoryService, NyaAuditLogger, BrokerDb> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nya_topic_{Guid.NewGuid():N}.db");
        var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        try
        {
            var store = new FixedConfigStore(new NyaChatConfig());
            var mem = new NyaMemoryService(db, store, NullLogger<NyaMemoryService>.Instance);
            var audit = new NyaAuditLogger(db, store, NullLogger<NyaAuditLogger>.Instance);
            body(mem, audit, db);
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
        }
    }

    private sealed class FixedConfigStore : INyaConfigStore<NyaChatConfig>
    {
        private readonly NyaChatConfig _c;
        public FixedConfigStore(NyaChatConfig c) { _c = c; }
        public NyaChatConfig Current => _c;
        public Task UpdateAsync(NyaChatConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static void AssertTrue(string name, bool cond, string? extra = null)
    {
        if (cond) { Console.WriteLine($"  [PASS] {name}"); _passed++; }
        else { Console.Error.WriteLine($"  [FAIL] {name}{(extra != null ? ": " + extra : "")}"); _failed++; }
    }
}
