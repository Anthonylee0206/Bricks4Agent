using Broker.NyaChat.Abstractions;
using BrokerCore.Data;

namespace Broker.NyaChat;

/// <summary>
/// 3 層記憶管理服務：短期訊息 / 結構化事實 / 長期摘要。
/// 所有寫入操作都帶版本控制；事實與摘要各支援最多 5 個版本的回滾。
/// </summary>
public class NyaMemoryService
{
    private readonly BrokerDb _db;
    private readonly INyaConfigStore<NyaChatConfig> _configStore;
    private NyaChatConfig _config => _configStore.Current; // 當前配置快照
    private readonly ILogger<NyaMemoryService> _logger;

    public NyaMemoryService(BrokerDb db, INyaConfigStore<NyaChatConfig> configStore, ILogger<NyaMemoryService> logger)
    {
        _db = db;
        _configStore = configStore;
        _logger = logger;

        _db.EnsureTable<NyaMessage>();
        _db.EnsureTable<NyaFact>();
        _db.EnsureTable<NyaSummary>();
        _db.EnsureTable<NyaTopic>();
        _db.EnsureTable<NyaSoulBinding>();
        _db.EnsureTable<NyaForgotten>();
        EnsureColumn("nya_forgotten", "match_terms", "TEXT");
        EnsureColumn("nya_forgotten", "through_sequence", "INTEGER");
        try { _db.Execute("CREATE INDEX IF NOT EXISTS ix_nya_messages_user_seq ON nya_messages(user_id, sequence)"); }
        catch (Exception ex) { _logger.LogWarning(ex, "[NyaMemory] create index failed"); }

        // 摘要依話題獨立→ nya_summaries 新增 topic_id。EnsureTable 為
        // CREATE TABLE IF NOT EXISTS，不會替既有表加欄；對舊資料庫做加性遷移（冪等）。
        EnsureColumn("nya_summaries", "topic_id", "TEXT");
        EnsureColumn("nya_facts", "source_text", "TEXT");
        EnsureColumn("nya_facts", "expires_at", "TEXT");
    }

    /// <summary>若指定表缺少該欄則 ALTER TABLE 加上（加性、冪等遷移）。</summary>
    private void EnsureColumn(string table, string column, string sqlType)
    {
        try
        {
            var has = _db.Scalar<int>(
                $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @column",
                new { column });
            if (has == 0)
                _db.Execute($"ALTER TABLE {table} ADD COLUMN {column} {sqlType}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaMemory] EnsureColumn {Table}.{Column} failed", table, column);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 短期記憶
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 寫入新訊息。回傳寫入後的訊息物件（含 sequence）。
    /// </summary>
    public NyaMessage AppendMessage(
        string userId,
        string role,
        string content,
        string? topicId = null,
        string? metadataJson = null)
    {
        // Fix A：序號分配原子化。
        // GetNextSequence（讀 MAX）與 Insert 必須在同一 _writeGate 持有區間內完成，否則同一使用者
        // 並發寫入會讀到相同 MAX、分配到重複序號。InTransaction 整段持有 _writeGate（BrokerDb），
        // 期間其他寫入/交易無法插入，即關閉此 read-then-write race（不依賴交易隔離級別）。
        return _db.InTransaction(() =>
        {
            var nextSeq = GetNextSequence(userId);
            var msg = new NyaMessage
            {
                MessageId = $"nyam_{Guid.NewGuid():N}"[..24],
                UserId = userId,
                Role = role,
                Content = content,
                Sequence = nextSeq,
                TopicId = topicId,
                IsDeleted = false,
                IsSummarized = false,
                MetadataJson = metadataJson ?? "{}",
                CreatedAt = DateTime.UtcNow
            };
            _db.Insert(msg);
            return msg;
        });
    }

    /// <summary>
    /// 取最近 N 則有效訊息（未刪除、未摘要）送入 LLM context。
    /// 被摘要涵蓋的訊息不會出現在這裡，確保不重複喂進 LLM。
    /// </summary>
    public List<NyaMessage> GetRecentMessages(string userId, int? limit = null, bool includeSummarized = false)
    {
        var lim = limit ?? _config.ShortTermMessageLimit;
        var inc = includeSummarized ? 1 : 0;
        return _db.Query<NyaMessage>(
            @"SELECT * FROM nya_messages
              WHERE user_id = @userId AND is_deleted = 0 AND (@inc = 1 OR is_summarized = 0)
              ORDER BY sequence DESC LIMIT @lim",
            new { userId, lim, inc })
            .OrderBy(m => m.Sequence)
            .ToList();
    }

    /// <summary>
    /// 取某話題最近 N 則有效訊息（對話依話題獨立）。
    /// 僅回傳該 topicId 的訊息，使切換話題後 LLM context 不混入其他話題。
    /// </summary>
    public List<NyaMessage> GetRecentMessages(string userId, string topicId, int? limit = null, bool includeSummarized = false)
    {
        var lim = limit ?? _config.ShortTermMessageLimit;
        var inc = includeSummarized ? 1 : 0;
        return _db.Query<NyaMessage>(
            @"SELECT * FROM nya_messages
              WHERE user_id = @userId AND topic_id = @topicId AND is_deleted = 0 AND (@inc = 1 OR is_summarized = 0)
              ORDER BY sequence DESC LIMIT @lim",
            new { userId, topicId, lim, inc })
            .OrderBy(m => m.Sequence)
            .ToList();
    }

    /// <summary>
    /// 取所有訊息（含已摘要），用於管理介面查詢。支援 limit + offset 分頁，
    /// 以及可選話題篩選（topicId 非空時只回該話題訊息）。
    /// </summary>
    public List<NyaMessage> GetAllMessages(string userId, int limit = 100, int offset = 0, string? topicId = null)
    {
        var hasTopic = !string.IsNullOrWhiteSpace(topicId);
        var sql = "SELECT * FROM nya_messages WHERE user_id = @userId AND is_deleted = 0"
                + (hasTopic ? " AND topic_id = @topicId" : "")
                + " ORDER BY sequence DESC LIMIT @limit OFFSET @offset";
        var p = new Dictionary<string, object?> { ["userId"] = userId, ["limit"] = limit, ["offset"] = offset };
        if (hasTopic) p["topicId"] = topicId;
        return _db.Query<NyaMessage>(sql, p);
    }

    /// <summary>取特定話題下的所有訊息</summary>
    public List<NyaMessage> GetMessagesByTopic(string userId, string topicId)
        => _db.Query<NyaMessage>(
            "SELECT * FROM nya_messages WHERE user_id = @userId AND topic_id = @topicId AND is_deleted = 0 ORDER BY sequence",
            new { userId, topicId });

    /// <summary>軟刪除單則訊息</summary>
    public bool SoftDeleteMessage(string userId, string messageId)
    {
        var affected = _db.Execute(
            "UPDATE nya_messages SET is_deleted = 1 WHERE message_id = @messageId AND user_id = @userId",
            new { messageId, userId });
        return affected > 0;
    }

    /// <summary>
    /// 批量軟刪除指定 ID 的訊息（最多 50 筆）。
    /// 僅刪除屬於該 userId 的訊息，防止跨用戶操作。
    /// </summary>
    public int DeleteMessagesByIds(string userId, IEnumerable<string> messageIds)
    {
        var ids = messageIds.Take(50).ToList();
        if (ids.Count == 0) return 0;

        // 修復：BaseOrm（非 Dapper）不會把 IEnumerable 展開成 IN (@id0, @id1, …)，
        // 直接綁 List 給 @ids 會讓 SQLite 無法解析 → 選取軟刪除一律失敗（#4 異常）。
        // 改為動態產生具名參數逐一展開，並以 Dictionary 傳入（AddParameters 支援 IDictionary）。
        var names = new List<string>(ids.Count);
        var p = new Dictionary<string, object?> { ["userId"] = userId };
        for (var i = 0; i < ids.Count; i++)
        {
            var key = $"id{i}";
            names.Add($"@{key}");
            p[key] = ids[i];
        }

        return _db.Execute(
            $"UPDATE nya_messages SET is_deleted = 1 WHERE user_id = @userId AND message_id IN ({string.Join(", ", names)})",
            p);
    }

    /// <summary>清除某使用者所有訊息（軟刪除），回傳本次新刪除的筆數（不重算已刪除者，與確認視窗筆數一致）</summary>
    public int DeleteAllMessages(string userId)
        => _db.Execute(
            "UPDATE nya_messages SET is_deleted = 1 WHERE user_id = @userId AND is_deleted = 0",
            new { userId });

    /// <summary>
    /// 將某話題指定 sequence 範圍的訊息標記為已摘要（不再送入 LLM）。
    /// sequence 為 user 全域遞增，故加上 topic_id 限制，避免摘要某話題時誤標其他話題訊息。
    /// </summary>
    public int MarkAsSummarized(string userId, string topicId, long fromSeq, long toSeq)
        => _db.Execute(
            @"UPDATE nya_messages SET is_summarized = 1
              WHERE user_id = @userId AND topic_id = @topicId
                AND sequence >= @fromSeq AND sequence <= @toSeq AND is_deleted = 0",
            new { userId, topicId, fromSeq, toSeq });

    /// <summary>統計未刪除訊息總數（含 is_summarized=true）；可選話題篩選。</summary>
    public int CountAllMessages(string userId, string? topicId = null)
    {
        var hasTopic = !string.IsNullOrWhiteSpace(topicId);
        var sql = "SELECT COUNT(*) FROM nya_messages WHERE user_id = @userId AND is_deleted = 0"
                + (hasTopic ? " AND topic_id = @topicId" : "");
        var p = new Dictionary<string, object?> { ["userId"] = userId };
        if (hasTopic) p["topicId"] = topicId;
        return _db.Scalar<int>(sql, p);
    }

    /// <summary>
    /// Fix 2（觸發透明度）：統計某話題的<b>未摘要(active)</b>訊息數（is_summarized=0 且未刪除）。
    /// 即自動摘要門檻（<see cref="NyaChatConfig.SummarizeTriggerCount"/>）實際計數的對象；
    /// 前端可由 <c>max(0, trigger - active)</c> 推算「距下次自動摘要還差幾則」。
    /// </summary>
    public int CountActiveMessages(string userId, string topicId)
        => _db.Scalar<int>(
            "SELECT COUNT(*) FROM nya_messages WHERE user_id = @userId AND topic_id = @topicId AND is_deleted = 0 AND is_summarized = 0",
            new { userId, topicId });

    // ═══════════════════════════════════════════════════════════════════════════
    // 話題管理（DB 持久化）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 取得或建立此 user 的 active 話題 ID。
    /// 若 DB 中已有 active 話題則直接回傳，否則建立第一個話題。
    /// 不依賴 in-memory session，重啟後狀態不丟失。
    /// </summary>
    public string GetOrCreateActiveTopic(string userId)
    {
        // Fix C：「查 active → 無則建立」原為兩段非原子（且 QueryFirst 未持 _writeGate），
        // 新使用者瞬時並發會各自讀到無 active 而建出多列話題，甚至與另一執行緒的交易撞連線。
        // 收進單一 _db.InTransaction（整段持 _writeGate）序列化，收斂為單列、不依賴交易隔離級別。
        return _db.InTransaction(() =>
        {
            var active = _db.QueryFirst<NyaTopic>(
                "SELECT * FROM nya_topics WHERE user_id = @userId AND is_active = 1",
                new { userId });
            return active?.TopicId ?? CreateNewTopicCore(userId, null).TopicId;
        });
    }

    /// <summary>建立新話題並設為 active，自動退役原本的 active 話題。</summary>
    public NyaTopic CreateNewTopic(string userId, string? title)
        => _db.InTransaction(() => CreateNewTopicCore(userId, title));

    /// <summary>
    /// 建立新話題的核心邏輯（退役舊 active → 計數 → 插入新 active）。
    /// **不帶自身交易**：必須在呼叫端的 _db.InTransaction 內執行（BaseDb 不支援巢狀交易）。
    /// 供 CreateNewTopic 與 GetOrCreateActiveTopic 共用，確保「查存在 + 建立」原子化。
    /// </summary>
    private NyaTopic CreateNewTopicCore(string userId, string? title)
    {
        _db.Execute(
            "UPDATE nya_topics SET is_active = 0 WHERE user_id = @userId AND is_active = 1",
            new { userId });

        var count = _db.Scalar<int>(
            "SELECT COUNT(*) FROM nya_topics WHERE user_id = @userId",
            new { userId });

        var topicNumber = count + 1;
        var prefix = TopicIdPrefix(userId);
        var topicId = $"{prefix}_topic_{topicNumber}";
        while (_db.Scalar<int>(
            "SELECT COUNT(*) FROM nya_topics WHERE topic_id = @topicId",
            new { topicId }) > 0)
        {
            topicNumber++;
            topicId = $"{prefix}_topic_{topicNumber}";
        }

        var topic = new NyaTopic
        {
            // topicID 格式 = User_id(前7碼) + "_topic_" + 遞增數字。
            TopicId   = topicId,
            UserId    = userId,
            Title     = !string.IsNullOrWhiteSpace(title) ? title : $"話題 {count + 1}",
            IsActive  = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Insert(topic);
        return topic;
    }

    /// <summary>取 userId 前 7 碼作 topicID 前綴，非英數字元以 '_' 取代（避免 ID 含分隔符/特殊字元）。</summary>
    private static string TopicIdPrefix(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return "user";
        var raw = userId.Length <= 7 ? userId : userId[..7];
        return new string(raw.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    }

    /// <summary>重新命名話題。回傳更新後的話題；找不到回傳 null。</summary>
    public NyaTopic? RenameTopic(string userId, string topicId, string title)
    {
        return _db.InTransaction<NyaTopic?>(() =>
        {
            var target = _db.QueryFirst<NyaTopic>(
                "SELECT * FROM nya_topics WHERE user_id = @userId AND topic_id = @topicId",
                new { userId, topicId });
            if (target == null) return null;

            _db.Execute(
                "UPDATE nya_topics SET title = @title WHERE user_id = @userId AND topic_id = @topicId",
                new { userId, topicId, title });
            target.Title = title;
            return target;
        });
    }

    /// <summary>依 topic_id 或標題在該 user 範圍解析出話題（供 LLM 工具以名稱切換）。找不到回傳 null。</summary>
    public NyaTopic? ResolveTopic(string userId, string topicIdOrTitle)
    {
        if (string.IsNullOrWhiteSpace(topicIdOrTitle)) return null;
        return _db.QueryFirst<NyaTopic>(
            @"SELECT * FROM nya_topics
              WHERE user_id = @userId AND (topic_id = @k OR title = @k)
              ORDER BY (topic_id = @k) DESC, created_at DESC
              LIMIT 1",
            new { userId, k = topicIdOrTitle });
    }

    /// <summary>切換 active 話題至指定 topicId，回傳切換後的話題；找不到則回傳 null。</summary>
    public NyaTopic? SwitchTopic(string userId, string topicId)
    {
        return _db.InTransaction<NyaTopic?>(() =>
        {
            var target = _db.QueryFirst<NyaTopic>(
                "SELECT * FROM nya_topics WHERE user_id = @userId AND topic_id = @topicId",
                new { userId, topicId });
            if (target == null) return null;

            _db.Execute(
                "UPDATE nya_topics SET is_active = 0 WHERE user_id = @userId AND is_active = 1",
                new { userId });
            _db.Execute(
                "UPDATE nya_topics SET is_active = 1 WHERE topic_id = @topicId",
                new { topicId });

            target.IsActive = true;
            return target;
        });
    }

    /// <summary>列出該 user 的所有話題（含各話題的訊息計數），依建立時間降序。</summary>
    public List<NyaTopicEntry> ListTopicsWithCount(string userId)
        => _db.Query<NyaTopicEntry>(
            @"SELECT t.topic_id, t.user_id, t.title, t.is_active, t.created_at,
                     COALESCE(mc.cnt, 0) AS msg_count
              FROM nya_topics t
              LEFT JOIN (
                  SELECT topic_id, COUNT(*) AS cnt
                  FROM nya_messages WHERE is_deleted = 0
                  GROUP BY topic_id
              ) mc ON mc.topic_id = t.topic_id
              WHERE t.user_id = @userId
              ORDER BY t.created_at DESC",
            new { userId });

    /// <summary>
    /// 刪除話題（管理端專用、不開放 LLM）：軟刪除該話題訊息、硬刪除該話題摘要、移除話題列。
    /// 若刪的是 active 話題，將最近建立的剩餘話題設為 active（無剩餘則下次對話自動建立）。
    /// 回傳是否確有刪除。
    /// </summary>
    public bool DeleteTopic(string userId, string topicId)
    {
        return _db.InTransaction(() =>
        {
            var target = _db.QueryFirst<NyaTopic>(
                "SELECT * FROM nya_topics WHERE user_id = @userId AND topic_id = @topicId",
                new { userId, topicId });
            if (target == null) return false;

            _db.Execute(
                "UPDATE nya_messages SET is_deleted = 1 WHERE user_id = @userId AND topic_id = @topicId",
                new { userId, topicId });
            _db.Execute(
                "DELETE FROM nya_summaries WHERE user_id = @userId AND topic_id = @topicId",
                new { userId, topicId });
            _db.Execute(
                "DELETE FROM nya_topics WHERE user_id = @userId AND topic_id = @topicId",
                new { userId, topicId });

            // 刪到 active → 指派新的 active 給最近的剩餘話題
            if (target.IsActive)
            {
                var next = _db.QueryFirst<NyaTopic>(
                    "SELECT * FROM nya_topics WHERE user_id = @userId ORDER BY created_at DESC LIMIT 1",
                    new { userId });
                if (next != null)
                    _db.Execute(
                        "UPDATE nya_topics SET is_active = 1 WHERE topic_id = @topicId",
                        new { topicId = next.TopicId });
            }

            return true;
        });
    }

    /// <summary>將訊息指派到話題（供手動 context 切換使用）</summary>
    public bool AssignTopic(string messageId, string topicId)
    {
        var affected = _db.Execute(
            "UPDATE nya_messages SET topic_id = @topicId WHERE message_id = @messageId",
            new { messageId, topicId });
        return affected > 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 結構化事實（帶版本回滾）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 新增或更新事實。
    /// 若同 (user_id, fact_key) 已存在 is_active=true 版本，則舊版退役，新版 version+1。
    /// 超過 FactVersionRetention 個版本時，自動刪除最舊版。
    /// </summary>
    public NyaFact UpsertFact(
        string userId,
        string category,
        string factKey,
        string factValue,
        string? sourceMessageId = null,
        float confidence = 1.0f,
        string? sourceText = null,
        DateTime? expiresAt = null)
    {
        return _db.InTransaction(() =>
        {
            _db.Execute("DELETE FROM nya_forgotten WHERE user_id = @userId AND fact_key = @factKey", new { userId, factKey });

            var existing = _db.Query<NyaFact>(
                "SELECT * FROM nya_facts WHERE user_id = @userId AND fact_key = @factKey ORDER BY version DESC",
                new { userId, factKey });

            var activeVersion = existing.FirstOrDefault(f => f.IsActive);
            var nextVersion = (existing.FirstOrDefault()?.Version ?? 0) + 1;

            // 退役舊版
            if (activeVersion != null)
            {
                _db.Execute(
                    "UPDATE nya_facts SET is_active = 0, superseded_at = @now WHERE fact_id = @factId",
                    new { now = DateTime.UtcNow, factId = activeVersion.FactId });
            }

            // 插入新版
            var newFact = new NyaFact
            {
                FactId = $"nyaf_{Guid.NewGuid():N}"[..24],
                UserId = userId,
                Category = category,
                FactKey = factKey,
                FactValue = factValue,
                Confidence = confidence,
                SourceMessageId = sourceMessageId,
                SourceText = sourceText,
                ExpiresAt = expiresAt,
                Version = nextVersion,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.Insert(newFact);

            // 清理超過版本上限的最舊版
            if (existing.Count >= _config.FactVersionRetention)
            {
                var toDelete = existing
                    .OrderBy(f => f.Version)
                    .Take(existing.Count - _config.FactVersionRetention + 1)
                    .ToList();
                foreach (var old in toDelete)
                {
                    _db.Execute(
                        "DELETE FROM nya_facts WHERE fact_id = @factId",
                        new { factId = old.FactId });
                }
            }

            return newFact;
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Soul 綁定（獨立於事實系統）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>取使用者目前綁定的 Soul ID（無綁定回傳 null）</summary>
    public string? GetSoulBinding(string userId)
        => _db.Query<NyaSoulBinding>(
                "SELECT * FROM nya_soul_bindings WHERE user_id = @userId",
                new { userId })
            .FirstOrDefault()?.SoulId;

    /// <summary>設定（新增或覆寫）使用者的 Soul 綁定</summary>
    public void SetSoulBinding(string userId, string soulId)
        => _db.Execute(
            "INSERT INTO nya_soul_bindings (user_id, soul_id, updated_at) VALUES (@userId, @soulId, @now) " +
            "ON CONFLICT(user_id) DO UPDATE SET soul_id = @soulId, updated_at = @now",
            new { userId, soulId, now = DateTime.UtcNow });

    /// <summary>取所有當前生效事實</summary>
    public List<NyaFact> GetActiveFacts(string userId)
    {
        var now = DateTime.UtcNow;
        return _db.Query<NyaFact>(
                "SELECT * FROM nya_facts WHERE user_id = @userId AND is_active = 1 ORDER BY category, fact_key",
                new { userId })
            .Where(f => f.ExpiresAt == null || f.ExpiresAt > now)
            .ToList();
    }

    /// <summary>依分類取生效事實</summary>
    public List<NyaFact> GetFactsByCategory(string userId, string category)
        => _db.Query<NyaFact>(
            "SELECT * FROM nya_facts WHERE user_id = @userId AND category = @category AND is_active = 1",
            new { userId, category });

    /// <summary>取某 fact_key 的所有版本歷史</summary>
    public List<NyaFact> GetFactHistory(string userId, string factKey)
        => _db.Query<NyaFact>(
            "SELECT * FROM nya_facts WHERE user_id = @userId AND fact_key = @factKey ORDER BY version DESC",
            new { userId, factKey });

    /// <summary>
    /// 回滾事實到指定版本（null = 上一版）。
    /// 將目前 active 版退役，將目標版重新設為 active。
    /// </summary>
    public NyaFact? RollbackFact(string userId, string factKey, int? targetVersion = null)
    {
        return _db.InTransaction<NyaFact?>(() =>
        {
            var history = GetFactHistory(userId, factKey);
            if (history.Count < 2)
                return null; // 沒有可回滾的版本

            var currentActive = history.FirstOrDefault(f => f.IsActive);
            NyaFact? rollbackTarget;

            if (targetVersion.HasValue)
            {
                rollbackTarget = history.FirstOrDefault(f => f.Version == targetVersion.Value && !f.IsActive);
            }
            else
            {
                // 回滾到上一個非 active 版本（版本號最大的非 active）
                rollbackTarget = history
                    .Where(f => !f.IsActive)
                    .OrderByDescending(f => f.Version)
                    .FirstOrDefault();
            }

            if (rollbackTarget == null)
                return null;

            // 退役當前版
            if (currentActive != null)
            {
                _db.Execute(
                    "UPDATE nya_facts SET is_active = 0, superseded_at = @now WHERE fact_id = @factId",
                    new { now = DateTime.UtcNow, factId = currentActive.FactId });
            }

            // 恢復目標版
            _db.Execute(
                "UPDATE nya_facts SET is_active = 1, superseded_at = NULL WHERE fact_id = @factId",
                new { factId = rollbackTarget.FactId });

            rollbackTarget.IsActive = true;
            rollbackTarget.SupersededAt = null;
            return rollbackTarget;
        });
    }

    /// <summary>清除某使用者所有事實</summary>
    public int DeleteAllFacts(string userId)
        => _db.Execute("DELETE FROM nya_facts WHERE user_id = @userId", new { userId });

    /// <summary>
    /// 軟退役（soft-retire）一個事實：把目前 active 版設 is_active=0 + superseded_at，<b>保留全部 history</b>，
    /// 可由 <see cref="ReactivateFact"/> 復原。與硬刪 <see cref="DeleteFact"/>（清 history、不可逆）不同——
    /// 自動調和（背景抽取）只用本法，不用硬刪。回 false = 該 key 無 active 版（no-op）。
    /// </summary>
    public bool RetireFact(string userId, string factKey, string reason)
    {
        var affected = _db.Execute(
            "UPDATE nya_facts SET is_active = 0, superseded_at = @now " +
            "WHERE user_id = @userId AND fact_key = @factKey AND is_active = 1",
            new { now = DateTime.UtcNow, userId, factKey });
        return affected > 0;
    }

    /// <summary>
    /// 復原一個被 <see cref="RetireFact"/> 軟退役的事實：把該 key 最新版重新設為 active。
    /// 回 false = 找不到該 key 任何版本。若已有 active 版則先退役它（保持單一 active 不變量）。
    /// </summary>
    public bool ReactivateFact(string userId, string factKey)
    {
        return _db.InTransaction(() =>
        {
            var history = GetFactHistory(userId, factKey); // version DESC
            if (history.Count == 0) return false;
            // 維持「每 key 至多一個 active」不變量。
            _db.Execute(
                "UPDATE nya_facts SET is_active = 0 WHERE user_id = @userId AND fact_key = @factKey AND is_active = 1",
                new { userId, factKey });
            _db.Execute(
                "UPDATE nya_facts SET is_active = 1, superseded_at = NULL WHERE fact_id = @factId",
                new { factId = history[0].FactId });
            return true;
        });
    }

    /// <summary>
    /// 遺忘：刪除該筆記的所有版本並留下遺忘紀錄（含各版本內容與使用者原話）。
    /// 遺忘之前、內容含這些字串的訊息，之後不會再出現在模型看得到的近期對話與搜尋結果；
    /// 遺忘之後使用者重新說的話照常可見。回傳是否有可遺忘的筆記。
    /// </summary>
    public bool ForgetFact(string userId, string factKey, bool coversPendingTurn = true)
    {
        return _db.InTransaction(() =>
        {
            var history = GetFactHistory(userId, factKey);
            if (history.Count == 0) return false;
            var value = (history.FirstOrDefault(f => f.IsActive) ?? history[0]).FactValue;
            var terms = history.SelectMany(f => new[] { f.FactValue, f.SourceText })
                .Where(t => !string.IsNullOrWhiteSpace(t) && t!.Trim().Length >= 2)
                .Select(t => t!.Trim()).Distinct().ToList();
            _db.Execute("DELETE FROM nya_facts WHERE user_id = @userId AND fact_key = @factKey", new { userId, factKey });
            _db.Execute("DELETE FROM nya_forgotten WHERE user_id = @userId AND fact_key = @factKey", new { userId, factKey });
            foreach (var term in terms)
                _db.Execute("DELETE FROM nya_summaries WHERE user_id = @userId AND summary_text LIKE @pattern ESCAPE '\\'",
                    new { userId, pattern = "%" + LikeEscape(term) + "%" });
            var through = GetNextSequence(userId) - 1 + (coversPendingTurn ? 2 : 0);
            _db.Insert(new NyaForgotten
            {
                ForgetId = $"nyag_{Guid.NewGuid():N}"[..24],
                UserId = userId,
                FactKey = factKey,
                FactValue = value,
                MatchTermsJson = System.Text.Json.JsonSerializer.Serialize(terms),
                ThroughSequence = through,
                CreatedAt = DateTime.UtcNow
            });
            return true;
        });
    }

    /// <summary>使用者要求遺忘的事項（新→舊）。</summary>
    public List<NyaForgotten> GetForgotten(string userId)
        => _db.Query<NyaForgotten>(
            "SELECT * FROM nya_forgotten WHERE user_id = @userId ORDER BY created_at DESC",
            new { userId });

    /// <summary>刪除某使用者的所有話題（清除全部記憶時使用）。</summary>
    public int DeleteAllTopics(string userId)
        => _db.Execute("DELETE FROM nya_topics WHERE user_id = @userId", new { userId });

    /// <summary>清除某使用者的遺忘紀錄。</summary>
    public int ClearForgotten(string userId)
        => _db.Execute("DELETE FROM nya_forgotten WHERE user_id = @userId", new { userId });

    private sealed record ForgetRule(DateTime CreatedAt, long ThroughSequence, IReadOnlyList<string> Terms);

    private List<ForgetRule> ForgetRules(string userId)
        => GetForgotten(userId).Select(g =>
        {
            var terms = new List<string>();
            try
            {
                if (!string.IsNullOrWhiteSpace(g.MatchTermsJson))
                    terms.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<string>>(g.MatchTermsJson!) ?? new());
            }
            catch (System.Text.Json.JsonException) { }
            if (g.FactValue.Trim().Length >= 2) terms.Add(g.FactValue.Trim());
            return new ForgetRule(AsUtc(g.CreatedAt), g.ThroughSequence, terms.Distinct().ToList());
        }).Where(r => r.Terms.Count > 0).ToList();

    private static DateTime AsUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private static bool IsForgotten(NyaMessage message, IReadOnlyList<ForgetRule> rules)
        => rules.Any(r => (r.ThroughSequence > 0 ? message.Sequence <= r.ThroughSequence : AsUtc(message.CreatedAt) <= r.CreatedAt) &&
            r.Terms.Any(t => message.Content.Contains(t, StringComparison.OrdinalIgnoreCase)));

    /// <summary>此話題是否為使用者的一般話題（LINE 話題標籤）；外部帶入的明確對話 ID 不在話題表中。</summary>
    public bool IsUserTopic(string userId, string? topicId)
        => !string.IsNullOrWhiteSpace(topicId) && _db.Scalar<int>(
            "SELECT COUNT(*) FROM nya_topics WHERE user_id = @userId AND topic_id = @topicId",
            new { userId, topicId }) > 0;

    private const string UserTopicScope =
        "(topic_id IS NULL OR topic_id IN (SELECT topic_id FROM nya_topics WHERE user_id = @userId))";

    /// <summary>
    /// 模型看得到的近期對話（舊→新）：<paramref name="conversationId"/> 為 null 時取使用者所有一般話題，
    /// 否則只取該明確對話。已遺忘的內容不會出現。
    /// </summary>
    public List<NyaMessage> GetRecentConversation(string userId, string? conversationId, int? limit = null, bool includeSummarized = false)
    {
        var lim = limit ?? _config.ShortTermMessageLimit;
        var scope = conversationId == null ? UserTopicScope : "topic_id = @conversationId";
        var rules = ForgetRules(userId);
        return _db.Query<NyaMessage>(
                $"SELECT * FROM nya_messages WHERE user_id = @userId AND is_deleted = 0 AND (@inc = 1 OR is_summarized = 0) AND {scope} ORDER BY sequence DESC LIMIT @lim",
                new { userId, conversationId, lim, inc = includeSummarized ? 1 : 0 })
            .Where(m => !IsForgotten(m, rules))
            .OrderBy(m => m.Sequence)
            .ToList();
    }

    private static string LikeEscape(string term)
        => term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// 搜尋使用者的原始對話（未刪除）。以空白或逗號分隔多個關鍵字，命中越多越前面，同分取較新者；
    /// <paramref name="conversationId"/> 為 null 時只搜一般話題，否則只搜該明確對話。已遺忘的內容不會回傳。
    /// 回傳命中訊息與緊接的下一則（下一則若已遺忘或不在同一範圍則為 null）。
    /// </summary>
    public List<(NyaMessage Hit, NyaMessage? Next)> SearchConversation(string userId, string query, int limit = 5, string? conversationId = null)
    {
        var terms = (query ?? "").Split(new[] { ' ', '\u3000', ',', '\uFF0C', '\u3001' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().Take(6).ToList();
        if (terms.Count == 0) return new();
        var p = new Dictionary<string, object?> { ["userId"] = userId, ["conversationId"] = conversationId };
        var likes = new List<string>();
        for (var i = 0; i < terms.Count; i++)
        {
            p[$"t{i}"] = "%" + LikeEscape(terms[i]) + "%";
            likes.Add($"(content LIKE @t{i} ESCAPE '\\')");
        }
        var scope = conversationId == null ? UserTopicScope : "topic_id = @conversationId";
        var rules = ForgetRules(userId);
        var hits = _db.Query<NyaMessage>(
                $"SELECT * FROM nya_messages WHERE user_id = @userId AND is_deleted = 0 AND {scope} AND ({string.Join(" OR ", likes)}) " +
                $"ORDER BY ({string.Join(" + ", likes)}) DESC, sequence DESC LIMIT 500", p)
            .Where(m => !IsForgotten(m, rules))
            .Take(Math.Clamp(limit, 1, 20))
            .ToList();
        return hits.Select(h =>
        {
            var next = _db.Query<NyaMessage>(
                $"SELECT * FROM nya_messages WHERE user_id = @userId AND is_deleted = 0 AND {scope} AND sequence > @sequence ORDER BY sequence LIMIT 1",
                new { userId, conversationId, sequence = h.Sequence }).FirstOrDefault();
            return (h, next != null && !IsForgotten(next, rules) ? next : null);
        }).ToList();
    }

    /// <summary>某話題中模型看得到的訊息（排除已遺忘內容），供摘要等模型用途。</summary>
    public List<NyaMessage> GetModelVisibleMessagesByTopic(string userId, string topicId)
    {
        var rules = ForgetRules(userId);
        return GetMessagesByTopic(userId, topicId).Where(m => !IsForgotten(m, rules)).ToList();
    }

    /// <summary>相容舊呼叫：只回命中訊息（一般話題範圍）。</summary>
    public List<NyaMessage> SearchMessages(string userId, string query, int limit = 5)
        => SearchConversation(userId, query, limit).Select(x => x.Hit).ToList();

    /// <summary>硬刪除單一 factKey 的所有版本（包含歷史版本）</summary>
    public int DeleteFact(string userId, string factKey)
        => _db.Execute(
            "DELETE FROM nya_facts WHERE user_id = @userId AND fact_key = @factKey",
            new { userId, factKey });

    // ═══════════════════════════════════════════════════════════════════════════
    // 長期摘要（帶版本回滾）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 建立新摘要，並自動退役該<b>話題</b>舊的 is_active 摘要。
    /// 摘要的版本鏈與 active 退役操作以 (user, topic) 為單位，
    /// 故摘要 B 話題時不會退役 A 話題的 active 摘要。
    /// </summary>
    public NyaSummary CreateSummary(
        string userId,
        string topicId,
        string summaryText,
        long coveredFromSeq,
        long coveredToSeq,
        int messageCount)
    {
        return _db.InTransaction(() =>
        {
            var existing = _db.Query<NyaSummary>(
                "SELECT * FROM nya_summaries WHERE user_id = @userId AND topic_id = @topicId ORDER BY version DESC",
                new { userId, topicId });

            var nextVersion = (existing.FirstOrDefault()?.Version ?? 0) + 1;

            // 退役該話題現有 active 摘要
            _db.Execute(
                "UPDATE nya_summaries SET is_active = 0, superseded_at = @now WHERE user_id = @userId AND topic_id = @topicId AND is_active = 1",
                new { now = DateTime.UtcNow, userId, topicId });

            var summary = new NyaSummary
            {
                SummaryId = $"nyas_{Guid.NewGuid():N}"[..24],
                UserId = userId,
                TopicId = topicId,
                SummaryText = summaryText,
                CoveredFromSeq = coveredFromSeq,
                CoveredToSeq = coveredToSeq,
                MessageCount = messageCount,
                Version = nextVersion,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.Insert(summary);

            // 清理超版本上限
            if (existing.Count >= _config.SummaryVersionRetention)
            {
                var toDelete = existing
                    .OrderBy(s => s.Version)
                    .Take(existing.Count - _config.SummaryVersionRetention + 1)
                    .ToList();
                foreach (var old in toDelete)
                {
                    _db.Execute(
                        "DELETE FROM nya_summaries WHERE summary_id = @summaryId",
                        new { summaryId = old.SummaryId });
                }
            }

            return summary;
        });
    }

    /// <summary>取使用者所有生效摘要（跨話題，供管理介面 / 總覽）。</summary>
    public List<NyaSummary> GetActiveSummaries(string userId)
        => _db.Query<NyaSummary>(
            "SELECT * FROM nya_summaries WHERE user_id = @userId AND is_active = 1 ORDER BY version",
            new { userId });

    /// <summary>
    /// 取某話題的生效摘要（摘要依話題獨立）。
    /// Fix（2026-06-11）：嚴格只取本話題摘要。先前納入 topic_id IS NULL 的舊版/全域摘要，
    /// 會讓單一全域摘要洩入<b>每個</b>話題的對話 context 與摘要合併輸入 → 跨話題汙染。
    /// 舊版全域摘要若需保留，請以資料遷移補上 topic_id，而非在查詢時跨話題回退。
    /// </summary>
    public List<NyaSummary> GetActiveSummaries(string userId, string topicId)
        => _db.Query<NyaSummary>(
            @"SELECT * FROM nya_summaries
              WHERE user_id = @userId AND is_active = 1 AND topic_id = @topicId
              ORDER BY version",
            new { userId, topicId });

    /// <summary>取所有版本歷史</summary>
    public List<NyaSummary> GetSummaryHistory(string userId)
        => _db.Query<NyaSummary>(
            "SELECT * FROM nya_summaries WHERE user_id = @userId ORDER BY version DESC",
            new { userId });

    /// <summary>回滾摘要到上一版（恢復 is_active=true，並取消已摘要訊息的標記）</summary>
    public NyaSummary? RollbackSummary(string userId, string summaryId)
    {
        return _db.InTransaction<NyaSummary?>(() =>
        {
            var history = GetSummaryHistory(userId);
            var target = history.FirstOrDefault(s => s.SummaryId == summaryId && !s.IsActive);

            if (target == null)
                return null;

            // 只退役同一話題中生效的摘要（不影響其他話題）
            foreach (var current in history.Where(s => s.IsActive && string.Equals(s.TopicId, target.TopicId, StringComparison.Ordinal)))
            {
                _db.Execute(
                    "UPDATE nya_summaries SET is_active = 0, superseded_at = @now WHERE summary_id = @summaryId",
                    new { now = DateTime.UtcNow, summaryId = current.SummaryId });

                // 還原被當前摘要標記為 is_summarized 的訊息（限該摘要所屬話題；topic 為 null 走舊版全域）
                _db.Execute(
                    @"UPDATE nya_messages SET is_summarized = 0
                      WHERE user_id = @userId AND sequence >= @fromSeq AND sequence <= @toSeq
                        AND (@topicId IS NULL OR topic_id = @topicId)",
                    new { userId, fromSeq = current.CoveredFromSeq, toSeq = current.CoveredToSeq, topicId = current.TopicId });
            }

            // 恢復目標版
            _db.Execute(
                "UPDATE nya_summaries SET is_active = 1, superseded_at = NULL WHERE summary_id = @summaryId",
                new { summaryId = target.SummaryId });

            // 重新標記目標版涵蓋範圍的訊息（同樣限該摘要所屬話題）
            _db.Execute(
                @"UPDATE nya_messages SET is_summarized = 1
                  WHERE user_id = @userId AND sequence >= @fromSeq AND sequence <= @toSeq AND is_deleted = 0
                    AND (@topicId IS NULL OR topic_id = @topicId)",
                new { userId, fromSeq = target.CoveredFromSeq, toSeq = target.CoveredToSeq, topicId = target.TopicId });

            target.IsActive = true;
            target.SupersededAt = null;
            return target;
        });
    }

    /// <summary>
    /// 清除某使用者所有摘要（硬刪除），同時重置訊息的 is_summarized 旗標，確保資料一致性。
    /// 與 RollbackSummary 對齊，在同一 transaction 中完成。
    /// </summary>
    public int DeleteAllSummaries(string userId)
        => _db.InTransaction(() =>
        {
            // 重置訊息的 is_summarized 旗標，避免邏輯斷層
            _db.Execute(
                "UPDATE nya_messages SET is_summarized = 0 WHERE user_id = @userId AND is_deleted = 0",
                new { userId });
            return _db.Execute("DELETE FROM nya_summaries WHERE user_id = @userId", new { userId });
        });

    // ═══════════════════════════════════════════════════════════════════════════
    // 管理 API 查詢（Admin Dashboard 專用）
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 列出所有有對話紀錄的使用者，回傳摘要清單與總數。
    /// 依最後訊息時間降序排列，支援分頁。
    /// </summary>
    public (List<NyaUserListEntry> Users, int Total) ListUsers(int limit = 50, int offset = 0, string? keyword = null)
    {
        // 關鍵字搜尋：對 user_id 做 LIKE %keyword% 過濾
        var keywordFilter = string.IsNullOrWhiteSpace(keyword)
            ? ""
            : " WHERE u.user_id LIKE @keyword";
        var keywordParam = $"%{keyword}%";
        const string knownUsers =
            "(SELECT user_id FROM nya_messages UNION SELECT user_id FROM nya_facts WHERE is_active = 1) u";

        var total = _db.Scalar<int>(
            $"SELECT COUNT(*) FROM {knownUsers}{keywordFilter}",
            string.IsNullOrWhiteSpace(keyword) ? null : new { keyword = keywordParam });

        var msgStats = _db.Query<NyaUserListEntry>(
            $@"SELECT u.user_id AS user_id,
                     COALESCE(SUM(CASE WHEN m.is_deleted = 0 THEN 1 ELSE 0 END), 0) AS msg_count,
                     MAX(CASE WHEN m.is_deleted = 0 THEN m.created_at END) AS last_at
              FROM {knownUsers} LEFT JOIN nya_messages m ON m.user_id = u.user_id{keywordFilter}
              GROUP BY u.user_id ORDER BY last_at IS NULL, last_at DESC LIMIT @limit OFFSET @offset",
            string.IsNullOrWhiteSpace(keyword)
                ? (object)new { limit, offset }
                : new { limit, offset, keyword = keywordParam });

        if (msgStats.Count == 0)
            return (new List<NyaUserListEntry>(), total);

        var factCounts = _db.Query<NyaUserFactCountRow>(
            "SELECT user_id, COUNT(*) AS fact_count FROM nya_facts WHERE is_active = 1 GROUP BY user_id");
        var factMap = factCounts.ToDictionary(x => x.UserId, x => x.FactCount);

        var souls = _db.Query<NyaUserSoulRow>(
            "SELECT user_id, soul_id AS soul_val FROM nya_soul_bindings");
        var soulMap = souls.ToDictionary(x => x.UserId, x => x.SoulVal);

        foreach (var entry in msgStats)
        {
            entry.ActiveFactCount = factMap.TryGetValue(entry.UserId, out var fc) ? fc : 0;
            entry.SoulId = soulMap.TryGetValue(entry.UserId, out var s) ? s : "default";
        }

        return (msgStats, total);
    }

    /// <summary>
    /// 取得單一使用者的完整記憶統計（一次 call 取代四個分頁 call）。
    /// </summary>
    public NyaUserOverview GetUserOverview(string userId)
    {
        var totalMsgs   = _db.Scalar<int>("SELECT COUNT(*) FROM nya_messages WHERE user_id = @userId AND is_deleted = 0", new { userId });
        var summarized  = _db.Scalar<int>("SELECT COUNT(*) FROM nya_messages WHERE user_id = @userId AND is_deleted = 0 AND is_summarized = 1", new { userId });
        var deleted     = _db.Scalar<int>("SELECT COUNT(*) FROM nya_messages WHERE user_id = @userId AND is_deleted = 1", new { userId });
        var lastMsgAt   = _db.Scalar<DateTime?>("SELECT MAX(created_at) FROM nya_messages WHERE user_id = @userId AND is_deleted = 0", new { userId });
        var lastFactAt  = _db.Scalar<DateTime?>("SELECT MAX(created_at) FROM nya_facts WHERE user_id = @userId AND is_active = 1", new { userId });

        var activeFacts     = GetActiveFacts(userId);
        var allSummaries    = GetSummaryHistory(userId);
        // 摘要依話題隔離 → 可有多筆 active（每話題一筆）。不再用 FirstOrDefault 取單一。
        var activeSummaries = allSummaries.Where(s => s.IsActive).ToList();
        var soulId          = GetSoulBinding(userId);

        var catCounts = activeFacts
            .GroupBy(f => f.Category)
            .ToDictionary(g => g.Key, g => g.Count());

        return new NyaUserOverview
        {
            UserId = userId,
            SoulId = soulId ?? "default",
            Messages = new NyaMsgStats
            {
                Total      = totalMsgs + deleted,
                Active     = totalMsgs - summarized,
                Summarized = summarized,
                Deleted    = deleted
            },
            Facts = new NyaFactStats
            {
                TotalActive = activeFacts.Count,
                Categories  = catCounts
            },
            Summaries = new NyaSummaryStats
            {
                TotalVersions      = allSummaries.Count,
                ActiveSummaryCount = activeSummaries.Count,
                LatestCoveredToSeq = activeSummaries.Count > 0 ? activeSummaries.Max(s => s.CoveredToSeq) : 0
            },
            LastMessageAt    = lastMsgAt,
            LastFactUpdatedAt = lastFactAt
        };
    }

    /// <summary>取得單則完整訊息（不截斷 content），找不到回傳 null。</summary>
    public NyaMessage? GetMessage(string userId, string messageId)
        => _db.QueryFirst<NyaMessage>(
            "SELECT * FROM nya_messages WHERE message_id = @messageId AND user_id = @userId",
            new { messageId, userId });

    // ═══════════════════════════════════════════════════════════════════════════
    // 工具方法
    // ═══════════════════════════════════════════════════════════════════════════

    private long GetNextSequence(string userId)
    {
        var maxSeq = _db.Scalar<long?>(
            "SELECT MAX(sequence) FROM nya_messages WHERE user_id = @userId",
            new { userId });
        return (maxSeq ?? 0) + 1;
    }

    public bool RemoveSoulBinding(string userId)
        => _db.Execute("DELETE FROM nya_soul_bindings WHERE user_id = @userId", new { userId }) > 0;

    public List<NyaFact> GetCurrentFacts(string userId)
        => _db.Query<NyaFact>(
            "SELECT * FROM nya_facts WHERE user_id = @userId AND is_active = 1 ORDER BY category, fact_key",
            new { userId });

    public int ClearForgotten(string userId, string factKey)
        => _db.Execute("DELETE FROM nya_forgotten WHERE user_id = @userId AND fact_key = @factKey", new { userId, factKey });
}
