using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

/// <summary>
/// 大項七（多用戶併發與狀態隔離）單元測試。
/// 鎖死：(A) AppendMessage 並發序號唯一性（Fix A）、(B) 序號語意/InTransaction 讀一致性前置驗證。
/// 背景摘要 per-user 去重（Fix B）依賴 LLM，以 live/integration 驗證（見 7-implementation-notes 驗證節）。
/// </summary>
public static class NyaConcurrencyTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0;
        _failed = 0;
        Console.WriteLine("=== NyaChat Concurrency Tests (大項七) ===");
        Console.WriteLine();

        TestInTransactionSeesMaxSequence();      // 前置：鎖死序號語意（D2 假設 + Fix A 回歸防線）
        TestConcurrentAppendUniqueSequence();    // Fix A：紅燈→綠燈
        TestConcurrentGetOrCreateActiveTopicSingleActive(); // Fix C：紅燈→綠燈
        TestConcurrentReadDuringTransaction();   // BrokerDb 讀取序列化：讀取不得搶用交易連線（+ 無狀態殘留）

        Console.WriteLine();
        Console.WriteLine($"=== Concurrency Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    // ── 序號語意（單執行緒、確定性；Fix A 改寫的回歸防線）─────────────────────────
    private static void TestInTransactionSeesMaxSequence()
    {
        Console.WriteLine("--- Sequence semantics (read consistency) ---");
        var (db, mem, path) = NewMemory();
        try
        {
            var m1 = mem.AppendMessage("u_tx", "user", "a");
            var m2 = mem.AppendMessage("u_tx", "user", "b");
            AssertTrue("seq-starts-at-1", m1.Sequence == 1);
            AssertTrue("seq-monotonic", m2.Sequence == m1.Sequence + 1);
        }
        finally { Cleanup(db, path); }
    }

    // ── Fix A：並發 AppendMessage 序號唯一性 ──────────────────────────────────────
    private static void TestConcurrentAppendUniqueSequence()
    {
        Console.WriteLine("--- Concurrent AppendMessage unique sequence (Fix A) ---");
        var (db, mem, path) = NewMemory();
        try
        {
            const int N = 200;
            var msgs = new NyaMessage[N];
            Parallel.For(0, N, i =>
            {
                msgs[i] = mem.AppendMessage("u_race", "user", $"m{i}");
            });

            var seqs = msgs.Select(m => m.Sequence).ToList();
            var distinct = seqs.Distinct().Count();
            AssertTrue("all-sequences-unique", distinct == N);
            AssertTrue("sequences-cover-1..N", seqs.Min() == 1 && seqs.Max() == N);

            // DB 內實際列數應等於 N（無覆寫/遺漏）。此為確定性主防線：
            // 即使序號偶然不撞，覆寫/遺漏仍會被列數抓到。
            var rows = mem.GetAllMessages("u_race", N + 10).Count;
            AssertTrue("row-count-matches", rows == N);
        }
        finally { Cleanup(db, path); }
    }

    // ── Fix C：並發 GetOrCreateActiveTopic 不建出多個 active 話題 ─────────────────
    // 新使用者「第一則訊息」的瞬時並發：原實作 QueryFirst(查 active) 未持寫閘，多執行緒同時讀到
    // 無 active → 各自 CreateNewTopic，建出多列話題（且最後僅一列 is_active=1，餘列被退役）。
    // 修法把「查存在 → 建立」收進單一 _db.InTransaction（整段持 _writeGate），序列化收斂為單列。
    private static void TestConcurrentGetOrCreateActiveTopicSingleActive()
    {
        Console.WriteLine("--- Concurrent GetOrCreateActiveTopic single active topic (Fix C) ---");
        var (db, mem, path) = NewMemory();
        try
        {
            const int N = 200;
            var topicIds = new string[N];
            Parallel.For(0, N, i =>
            {
                topicIds[i] = mem.GetOrCreateActiveTopic("u_topic_race");
            });

            // 全部呼叫應拿到同一個話題 id。
            var distinctIds = topicIds.Distinct().Count();
            AssertTrue("all-return-same-topic", distinctIds == 1);

            // 確定性主防線：DB 內該使用者只應有「一列」話題（即使 is_active 偶然收斂，
            // 多餘的退役話題列仍會被總列數抓到）。
            var totalRows = db.Query<NyaTopic>(
                "SELECT * FROM nya_topics WHERE user_id = @userId",
                new { userId = "u_topic_race" }).Count;
            AssertTrue("exactly-one-topic-row", totalRows == 1);

            // is_active=1 的話題恰為一列。
            var activeRows = db.Query<NyaTopic>(
                "SELECT * FROM nya_topics WHERE user_id = @userId AND is_active = 1",
                new { userId = "u_topic_race" }).Count;
            AssertTrue("exactly-one-active-topic", activeRows == 1);
        }
        finally { Cleanup(db, path); }
    }

    // ── BrokerDb 讀取序列化：讀取與交易並發不得損壞連線/殘留交易狀態 ──────────────
    // 根因：BaseDb 交易進行中以單一共享連線承載，GetConnection() 對任一執行緒只要 _transaction!=null
    // 即回傳該交易連線；若讀取不經 _writeGate，背景交易（Thread B）進行中、另一執行緒（Thread A）的讀取
    // 會搶用同一條 SQLite 連線並發操作 → 損壞/例外，甚至殘留交易狀態 → 後續一律 "A transaction is already active."。
    // 修法：BrokerDb 讀取也經 _writeGate 序列化。本測試以讀寫交易並發壓測，斷言無例外且 DB 仍可用。
    private static void TestConcurrentReadDuringTransaction()
    {
        Console.WriteLine("--- Concurrent reads racing transactions: no corruption / no state leak ---");
        var (db, mem, path) = NewMemory();
        try
        {
            const string u = "u_rw_race";
            for (var i = 0; i < 10; i++) mem.AppendMessage(u, "user", $"seed{i}");

            const int N = 240;
            Exception? failure = null;
            var gate = new object();
            Parallel.For(0, N, i =>
            {
                try
                {
                    if (i % 3 == 0)
                        // 寫入交易（持 _writeGate 全程）：AppendMessage 內含 GetNextSequence(讀)+Insert(寫) 於同一交易。
                        mem.AppendMessage(u, "user", $"w{i}");
                    else
                        // 純讀取：修好後應序列化、不得搶用交易連線。
                        db.Query<NyaMessage>(
                            "SELECT * FROM nya_messages WHERE user_id = @u ORDER BY sequence", new { u });
                }
                catch (Exception ex)
                {
                    lock (gate) { failure ??= ex; }
                }
            });

            AssertTrue("concurrent-read-write-no-exception", failure == null);

            // 交易狀態未殘留（Defect 2 回歸）：後續交易仍可正常開始/提交。
            var leaked = false;
            try { mem.AppendMessage(u, "user", "after"); }
            catch { leaked = true; }
            AssertTrue("no-transaction-state-leak", !leaked);

            // 資料一致：10 seed + (N/3 個寫入) + 1 after，全部可讀回且序號唯一。
            var rows = db.Query<NyaMessage>("SELECT * FROM nya_messages WHERE user_id = @u", new { u });
            AssertTrue("rows-readable-and-unique-seq", rows.Count == rows.Select(r => r.Sequence).Distinct().Count());
        }
        finally { Cleanup(db, path); }
    }

    // ── 測試基礎設施 ─────────────────────────────────────────────────────────────

    // 每個測試用獨立暫存 SQLite 檔，測完即刪（登記於 CLAUDE.md 測試產物表：nya_concurrency_*.db*）。
    private static (BrokerDb db, NyaMemoryService mem, string path) NewMemory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nya_concurrency_{Guid.NewGuid():N}.db");
        // Pooling=False：確保 Dispose() 真正關閉連線並釋放檔案 handle，否則 Microsoft.Data.Sqlite
        // 的連線池會保留 handle 導致 Cleanup 的 File.Delete 靜默失敗、暫存檔殘留（違反 CLAUDE.md 清理鐵則）。
        var db = BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        var store = new FixedConfigStore(new NyaChatConfig());
        var mem = new NyaMemoryService(db, store, NullLogger<NyaMemoryService>.Instance);
        return (db, mem, path);
    }

    private static void Cleanup(BrokerDb db, string path)
    {
        db.Dispose();
        foreach (var p in new[] { path, path + "-shm", path + "-wal" })
            try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort */ }
    }

    // ── 測試替身（與 NyaTokenBudgetTests / NyaPromptTests 同款）──────────────────
    private sealed class FixedConfigStore : INyaConfigStore<NyaChatConfig>
    {
        private readonly NyaChatConfig _c;
        public FixedConfigStore(NyaChatConfig c) { _c = c; }
        public NyaChatConfig Current => _c;
        public Task UpdateAsync(NyaChatConfig config, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // ── assert helper ────────────────────────────────────────────────────────────
    private static void AssertTrue(string name, bool cond)
    {
        if (cond) { Console.WriteLine($"  [PASS] {name}"); _passed++; }
        else { Console.Error.WriteLine($"  [FAIL] {name}: expected true"); _failed++; }
    }
}
