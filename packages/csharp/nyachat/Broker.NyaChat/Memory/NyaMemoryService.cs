using Broker.NyaChat.Abstractions;
using BrokerCore.Data;

namespace Broker.NyaChat;

public partial class NyaMemoryService
{
    private readonly BrokerDb _db;
    private readonly INyaConfigStore<NyaChatConfig> _configStore;
    private NyaChatConfig _config => _configStore.Current;
    private readonly ILogger<NyaMemoryService> _logger;

    private long _sqlReadCount;
    public long SqlReadCount => Interlocked.Read(ref _sqlReadCount);

    private List<T> ReadQuery<T>(string sql, object? parameters = null) where T : new()
    {
        Interlocked.Increment(ref _sqlReadCount);
        return _db.Query<T>(sql, parameters);
    }

    private T? ReadFirst<T>(string sql, object? parameters = null) where T : class, new()
    {
        Interlocked.Increment(ref _sqlReadCount);
        return _db.QueryFirst<T>(sql, parameters);
    }

    private T? ReadScalar<T>(string sql, object? parameters = null)
    {
        Interlocked.Increment(ref _sqlReadCount);
        return _db.Scalar<T>(sql, parameters);
    }

    public NyaMemoryService(BrokerDb db, INyaConfigStore<NyaChatConfig> configStore, ILogger<NyaMemoryService> logger)
    {
        _db = db;
        _configStore = configStore;
        _logger = logger;

        _db.EnsureTable<NyaMessage>();
        _db.EnsureTable<NyaSummary>();
        _db.EnsureTable<NyaTopic>();
        _db.EnsureTable<NyaSoulBinding>();
        try { _db.Execute("CREATE INDEX IF NOT EXISTS ix_nya_messages_user_seq ON nya_messages(user_id, sequence)"); }
        catch (Exception ex) { _logger.LogWarning(ex, "[NyaMemory] create index failed"); }

        EnsureColumn("nya_summaries", "topic_id", "TEXT");
        InitializeTurnMemory();
    }

    private void EnsureColumn(string table, string column, string sqlType)
    {
        try
        {
            var has = ReadScalar<int>(
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

    public NyaMessage AppendMessage(
        string userId,
        string role,
        string content,
        string? topicId = null,
        string? metadataJson = null)
    {

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

    public List<NyaMessage> GetRecentMessages(string userId, int? limit = null, bool includeSummarized = false)
    {
        var lim = limit ?? _config.ShortTermMessageLimit;
        var inc = includeSummarized ? 1 : 0;
        return ReadQuery<NyaMessage>(
            @"SELECT * FROM nya_messages
              WHERE user_id = @userId AND is_deleted = 0 AND (@inc = 1 OR is_summarized = 0)
              ORDER BY sequence DESC LIMIT @lim",
            new { userId, lim, inc })
            .OrderBy(m => m.Sequence)
            .ToList();
    }

    public List<NyaMessage> GetRecentMessages(string userId, string topicId, int? limit = null, bool includeSummarized = false)
    {
        var lim = limit ?? _config.ShortTermMessageLimit;
        var inc = includeSummarized ? 1 : 0;
        return ReadQuery<NyaMessage>(
            @"SELECT * FROM nya_messages
              WHERE user_id = @userId AND topic_id = @topicId AND is_deleted = 0 AND (@inc = 1 OR is_summarized = 0)
              ORDER BY sequence DESC LIMIT @lim",
            new { userId, topicId, lim, inc })
            .OrderBy(m => m.Sequence)
            .ToList();
    }

    public List<NyaMessage> GetAllMessages(string userId, int limit = 100, int offset = 0, string? topicId = null, bool unassigned = false)
    {
        var hasTopic = !string.IsNullOrWhiteSpace(topicId);
        var sql = "SELECT * FROM nya_messages WHERE user_id = @userId AND is_deleted = 0"
                + (hasTopic ? " AND topic_id = @topicId" : "")
                + (unassigned ? " AND topic_id IS NULL" : "")
                + " ORDER BY sequence DESC LIMIT @limit OFFSET @offset";
        var p = new Dictionary<string, object?> { ["userId"] = userId, ["limit"] = limit, ["offset"] = offset };
        if (hasTopic) p["topicId"] = topicId;
        return ReadQuery<NyaMessage>(sql, p);
    }

    public List<NyaMessage> GetMessagesByTopic(string userId, string topicId)
        => ReadQuery<NyaMessage>(
            "SELECT * FROM nya_messages WHERE user_id = @userId AND topic_id = @topicId AND is_deleted = 0 ORDER BY sequence",
            new { userId, topicId });

    public bool SoftDeleteMessage(string userId, string messageId)
    {
        var affected = _db.Execute(
            "UPDATE nya_messages SET is_deleted = 1 WHERE message_id = @messageId AND user_id = @userId",
            new { messageId, userId });
        return affected > 0;
    }

    public int DeleteMessagesByIds(string userId, IEnumerable<string> messageIds)
    {
        var ids = messageIds.Take(50).ToList();
        if (ids.Count == 0) return 0;

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

    public int DeleteAllMessages(string userId)
        => _db.Execute(
            "UPDATE nya_messages SET is_deleted = 1 WHERE user_id = @userId AND is_deleted = 0",
            new { userId });

    public int MarkAsSummarized(string userId, string topicId, long fromSeq, long toSeq)
        => _db.Execute(
            @"UPDATE nya_messages SET is_summarized = 1
              WHERE user_id = @userId AND topic_id = @topicId
                AND sequence >= @fromSeq AND sequence <= @toSeq AND is_deleted = 0",
            new { userId, topicId, fromSeq, toSeq });

    public int CountAllMessages(string userId, string? topicId = null, bool unassigned = false)
    {
        var hasTopic = !string.IsNullOrWhiteSpace(topicId);
        var sql = "SELECT COUNT(*) FROM nya_messages WHERE user_id = @userId AND is_deleted = 0"
                + (hasTopic ? " AND topic_id = @topicId" : "")
                + (unassigned ? " AND topic_id IS NULL" : "");
        var p = new Dictionary<string, object?> { ["userId"] = userId };
        if (hasTopic) p["topicId"] = topicId;
        return ReadScalar<int>(sql, p);
    }

    public int CountActiveMessages(string userId, string topicId)
        => ReadScalar<int>(
            "SELECT COUNT(*) FROM nya_messages WHERE user_id = @userId AND topic_id = @topicId AND is_deleted = 0 AND is_summarized = 0",
            new { userId, topicId });

    public NyaTopic? GetActiveTopic(string userId)
        => ReadFirst<NyaTopic>("SELECT * FROM nya_topics WHERE user_id = @userId AND is_active = 1", new { userId });

    public List<string> GetExternalConversationIds(string userId)
        => ReadQuery<NyaMessage>(
            "SELECT DISTINCT conversation_id AS topic_id FROM nya_messages WHERE user_id = @userId AND scope = 'external' AND conversation_id IS NOT NULL AND is_deleted = 0 ORDER BY conversation_id", new { userId })
            .Select(m => m.TopicId!).ToList();

    public IReadOnlyDictionary<string, string[]> DiagnoseMessages(string userId, IEnumerable<NyaMessage> messages, HashSet<string>? selectedIds = null)
    {
        var active = GetActiveTopic(userId);
        var candidates = selectedIds ?? (active == null ? new HashSet<string>() : SelectRecentHistory(new NyaToolContext { UserId = userId }, active.TopicId).Select(m => m.MessageId).ToHashSet());
        var windowIds = active == null ? new HashSet<string>() : SelectRecentHistory(new NyaToolContext { UserId = userId }, active.TopicId).Select(m => m.MessageId).ToHashSet();
        var refs = GetHideReferences(userId).Where(r => r.IsActive).ToList();
        var hiddenIds = refs.Where(r => r.Kind == "message").Select(r => r.TargetId).ToHashSet();
        var hiddenTurns = refs.Where(r => r.Kind == "turn").Select(r => r.TargetId).ToHashSet();
        return messages.ToDictionary(m => m.MessageId, m =>
        {
            var reasons = new List<string>();
            var forgotten = hiddenIds.Contains(m.MessageId) || hiddenTurns.Contains(m.TurnId);
            if (forgotten) reasons.Add("forgotten");
            if (active == null || m.Scope != "normal" || m.TopicId != active.TopicId) reasons.Add("other_conversation");
            else if (!forgotten && !candidates.Contains(m.MessageId))
                reasons.Add(_config.AutoSummarizeEnabled && m.IsSummarized ? "summarized" : windowIds.Contains(m.MessageId) ? "context_budget" : "outside_recent_window");
            return reasons.ToArray();
        });
    }

    public string GetOrCreateActiveTopic(string userId)
    {

        return _db.InTransaction(() =>
        {
            var active = ReadFirst<NyaTopic>(
                "SELECT * FROM nya_topics WHERE user_id = @userId AND is_active = 1",
                new { userId });
            return active?.TopicId ?? CreateNewTopicCore(userId, null).TopicId;
        });
    }

    public NyaTopic CreateNewTopic(string userId, string? title)
        => _db.InTransaction(() => CreateNewTopicCore(userId, title));

    private NyaTopic CreateNewTopicCore(string userId, string? title)
    {
        if (title?.EnumerateRunes().Count() > 128) throw new NyaMemoryException("title_length", "title", "聊天室標題上限 128 字。");
        if (ReadScalar<int>("SELECT COUNT(*) FROM nya_topics WHERE user_id = @userId", new { userId }) >= _config.TopicCapacity)
            throw new NyaMemoryException("topic_capacity", "title", "聊天室已滿，請先整理。");
        _db.Execute(
            "UPDATE nya_topics SET is_active = 0 WHERE user_id = @userId AND is_active = 1",
            new { userId });

        var count = ReadScalar<int>(
            "SELECT COUNT(*) FROM nya_topics WHERE user_id = @userId",
            new { userId });

        var topicNumber = count + 1;
        var prefix = TopicIdPrefix(userId);
        var topicId = $"{prefix}_topic_{topicNumber}";
        while (ReadScalar<int>(
            "SELECT COUNT(*) FROM nya_topics WHERE topic_id = @topicId",
            new { topicId }) > 0)
        {
            topicNumber++;
            topicId = $"{prefix}_topic_{topicNumber}";
        }

        var topic = new NyaTopic
        {

            TopicId   = topicId,
            UserId    = userId,
            Title     = !string.IsNullOrWhiteSpace(title) ? title : $"話題 {count + 1}",
            IsActive  = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Insert(topic);
        return topic;
    }

    private static string TopicIdPrefix(string userId)
    {
        if (string.IsNullOrEmpty(userId)) return "user";
        var raw = userId.Length <= 7 ? userId : userId[..7];
        return new string(raw.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
    }

    public NyaTopic? RenameTopic(string userId, string topicId, string title, string? turnId = null)
    {
        if (string.IsNullOrWhiteSpace(title) || title.EnumerateRunes().Count() > 128) throw new NyaMemoryException("title_length", "title", "聊天室標題須為 1 至 128 字。");
        return _db.InTransaction<NyaTopic?>(() =>
        {
            var target = ReadFirst<NyaTopic>(
                "SELECT * FROM nya_topics WHERE user_id = @userId AND topic_id = @topicId",
                new { userId, topicId });
            if (target == null) return null;

            _db.Execute(
                "UPDATE nya_topics SET title = @title WHERE user_id = @userId AND topic_id = @topicId",
                new { userId, topicId, title });
            target.Title = title;
            if (turnId != null) RecordTurnEffect(userId, turnId, new { action = "rename_topic", topic_id = topicId });
            return target;
        });
    }

    public NyaTopic? ResolveTopic(string userId, string topicIdOrTitle)
    {
        if (string.IsNullOrWhiteSpace(topicIdOrTitle)) return null;
        return ReadFirst<NyaTopic>(
            @"SELECT * FROM nya_topics
              WHERE user_id = @userId AND (topic_id = @k OR title = @k)
              ORDER BY (topic_id = @k) DESC, created_at DESC
              LIMIT 1",
            new { userId, k = topicIdOrTitle });
    }

    public NyaTopic? SwitchTopic(string userId, string topicId)
    {
        return _db.InTransaction<NyaTopic?>(() =>
        {
            var target = ReadFirst<NyaTopic>(
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

    public List<NyaTopicEntry> ListTopicsWithCount(string userId)
        => ReadQuery<NyaTopicEntry>(
            @"SELECT t.topic_id, t.user_id, t.title, t.is_active, t.created_at,
                     COALESCE(mc.cnt, 0) AS msg_count
              FROM nya_topics t
              LEFT JOIN (
                  SELECT topic_id, COUNT(*) AS cnt
                  FROM nya_messages WHERE is_deleted = 0 AND scope = 'normal' AND user_id = @userId
                  GROUP BY topic_id
              ) mc ON mc.topic_id = t.topic_id
              WHERE t.user_id = @userId
              ORDER BY t.created_at DESC",
            new { userId });

    public bool DeleteTopic(string userId, string topicId)
    {
        return _db.InTransaction(() =>
        {
            var target = ReadFirst<NyaTopic>(
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

            if (target.IsActive)
            {
                var next = ReadFirst<NyaTopic>(
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

    public bool AssignTopic(string messageId, string topicId)
    {
        var affected = _db.Execute(
            "UPDATE nya_messages SET topic_id = @topicId WHERE message_id = @messageId",
            new { messageId, topicId });
        return affected > 0;
    }

    public string? GetSoulBinding(string userId)
        => ReadQuery<NyaSoulBinding>(
                "SELECT * FROM nya_soul_bindings WHERE user_id = @userId",
                new { userId })
            .FirstOrDefault()?.SoulId;

    public void SetSoulBinding(string userId, string soulId)
        => _db.Execute(
            "INSERT INTO nya_soul_bindings (user_id, soul_id, updated_at) VALUES (@userId, @soulId, @now) " +
            "ON CONFLICT(user_id) DO UPDATE SET soul_id = @soulId, updated_at = @now",
            new { userId, soulId, now = DateTime.UtcNow });

    public int DeleteAllTopics(string userId)
        => _db.Execute("DELETE FROM nya_topics WHERE user_id = @userId", new { userId });

    public bool IsUserTopic(string userId, string? topicId)
        => !string.IsNullOrWhiteSpace(topicId) && ReadScalar<int>(
            "SELECT COUNT(*) FROM nya_topics WHERE user_id = @userId AND topic_id = @topicId",
            new { userId, topicId }) > 0;

    public List<NyaMessage> GetModelVisibleMessagesByTopic(string userId, string topicId, string scope = "normal", string? externalConversationId = null)
    {
        return VisibleMessages(new NyaToolContext { UserId = userId, Scope = scope, ExternalConversationId = externalConversationId }, topicId);
    }

    public NyaSummary CreateSummary(
        string userId,
        string topicId,
        string summaryText,
        long coveredFromSeq,
        long coveredToSeq,
        int messageCount,
        long? expectedVisibilityVersion = null,
        string[]? expectedActiveSummaryIds = null,
        string scope = "normal",
        string? externalConversationId = null)
    {
        return _db.InTransaction(() =>
        {
            if (expectedVisibilityVersion.HasValue && expectedVisibilityVersion.Value != VisibilityVersion(userId))
                throw new NyaMemoryException("visibility_changed", "summary", "摘要來源可見性已改變，拒絕提交。");
            var existing = ReadQuery<NyaSummary>(
                "SELECT * FROM nya_summaries WHERE user_id = @userId AND topic_id = @topicId ORDER BY version DESC",
                new { userId, topicId }).Where(s => SameScope(scope, externalConversationId, s.Scope, s.ConversationId)).ToList();
            if (expectedActiveSummaryIds != null && !existing.Where(s => s.IsActive).Select(s => s.SummaryId).Order().SequenceEqual(expectedActiveSummaryIds.Order()))
                throw new NyaMemoryException("summary_changed", "summary", "摘要版本已改變，拒絕提交。");
            var dependencies = existing.Where(s => s.IsActive).SelectMany(s => Ids(s.SourceMessageIdsJson))
                .Concat(GetMessagesByTopic(userId, topicId).Where(m => SameScope(scope, externalConversationId, m.Scope, m.ConversationId) && m.Sequence >= coveredFromSeq && m.Sequence <= coveredToSeq).Select(m => m.MessageId)).Distinct().ToArray();
            if (dependencies.Any(id => IsHidden(userId, "message", id)))
                throw new NyaMemoryException("source_hidden", "summary", "摘要來源已隱藏，拒絕提交。");

            var nextVersion = (existing.FirstOrDefault()?.Version ?? 0) + 1;

            foreach (var current in existing.Where(s => s.IsActive))
                _db.Execute("UPDATE nya_summaries SET is_active = 0, superseded_at = @now WHERE summary_id = @id", new { now = DateTime.UtcNow, id = current.SummaryId });

            var summary = new NyaSummary
            {
                SummaryId = $"nyas_{Guid.NewGuid():N}"[..24],
                UserId = userId,
                TopicId = topicId,
                SummaryText = summaryText,
                Scope = scope, ConversationId = externalConversationId,
                SourceMessageIdsJson = System.Text.Json.JsonSerializer.Serialize(dependencies), VisibilityVersion = VisibilityVersion(userId),
                CoveredFromSeq = coveredFromSeq,
                CoveredToSeq = coveredToSeq,
                MessageCount = messageCount,
                Version = nextVersion,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.Insert(summary);

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

    private const string SummarySourcesVisible = @"NOT EXISTS (SELECT 1 FROM json_each(s.source_message_ids) d
        LEFT JOIN nya_messages m ON m.message_id = d.value AND m.user_id = s.user_id
        WHERE m.message_id IS NULL OR m.is_deleted = 1 OR EXISTS (SELECT 1 FROM nya_hide_refs h
            WHERE h.user_id = s.user_id AND h.is_active = 1 AND
                ((h.kind = 'message' AND h.target_id = d.value) OR (h.kind = 'turn' AND h.target_id = m.turn_id))))";

    public HashSet<string> InvalidSummaryIds(string userId)
        => ReadQuery<NyaSummary>("SELECT s.* FROM nya_summaries s WHERE s.user_id = @userId AND NOT (" + SummarySourcesVisible + ")", new { userId })
            .Select(s => s.SummaryId).ToHashSet();

    public List<NyaSummary> GetActiveSummaries(string userId)
        => ReadQuery<NyaSummary>("SELECT s.* FROM nya_summaries s WHERE s.user_id = @userId AND s.is_active = 1 AND " + SummarySourcesVisible + " ORDER BY s.version", new { userId });

    public List<NyaSummary> GetActiveSummaries(string userId, string topicId)
        => ReadQuery<NyaSummary>("SELECT s.* FROM nya_summaries s WHERE s.user_id = @userId AND s.is_active = 1 AND s.topic_id = @topicId AND " + SummarySourcesVisible + " ORDER BY s.version", new { userId, topicId });

    public List<NyaSummary> GetSummaryHistory(string userId)
        => ReadQuery<NyaSummary>(
            "SELECT * FROM nya_summaries WHERE user_id = @userId ORDER BY version DESC",
            new { userId });

    public NyaSummary? RollbackSummary(string userId, string summaryId)
    {
        return _db.InTransaction<NyaSummary?>(() =>
        {
            var history = GetSummaryHistory(userId);
            var target = history.FirstOrDefault(s => s.SummaryId == summaryId && !s.IsActive);

            if (target == null || Ids(target.SourceMessageIdsJson).Any(id => IsHidden(userId, "message", id)))
                return null;

            foreach (var current in history.Where(s => s.IsActive && string.Equals(s.TopicId, target.TopicId, StringComparison.Ordinal)))
            {
                _db.Execute(
                    "UPDATE nya_summaries SET is_active = 0, superseded_at = @now WHERE summary_id = @summaryId",
                    new { now = DateTime.UtcNow, summaryId = current.SummaryId });

                _db.Execute(
                    @"UPDATE nya_messages SET is_summarized = 0
                      WHERE user_id = @userId AND sequence >= @fromSeq AND sequence <= @toSeq
                        AND (@topicId IS NULL OR topic_id = @topicId)",
                    new { userId, fromSeq = current.CoveredFromSeq, toSeq = current.CoveredToSeq, topicId = current.TopicId });
            }

            _db.Execute(
                "UPDATE nya_summaries SET is_active = 1, superseded_at = NULL WHERE summary_id = @summaryId",
                new { summaryId = target.SummaryId });

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

    public int DeleteAllSummaries(string userId)
        => _db.InTransaction(() =>
        {

            _db.Execute(
                "UPDATE nya_messages SET is_summarized = 0 WHERE user_id = @userId AND is_deleted = 0",
                new { userId });
            return _db.Execute("DELETE FROM nya_summaries WHERE user_id = @userId", new { userId });
        });

    public (List<NyaUserListEntry> Users, int Total) ListUsers(int limit = 50, int offset = 0, string? keyword = null)
    {

        var keywordFilter = string.IsNullOrWhiteSpace(keyword)
            ? ""
            : " WHERE u.user_id LIKE @keyword";
        var keywordParam = $"%{keyword}%";
        const string knownUsers =
            "(SELECT user_id FROM nya_messages UNION SELECT user_id FROM nya_topics UNION SELECT user_id FROM nya_soul_bindings UNION SELECT user_id FROM nya_notes UNION SELECT user_id FROM nya_turns) u";

        var total = ReadScalar<int>(
            $"SELECT COUNT(*) FROM {knownUsers}{keywordFilter}",
            string.IsNullOrWhiteSpace(keyword) ? null : new { keyword = keywordParam });

        var msgStats = ReadQuery<NyaUserListEntry>(
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

        var souls = ReadQuery<NyaUserSoulRow>(
            "SELECT user_id, soul_id AS soul_val FROM nya_soul_bindings");
        var soulMap = souls.ToDictionary(x => x.UserId, x => x.SoulVal);

        foreach (var entry in msgStats)
        {
            entry.ActiveNoteCount = ListNotes(entry.UserId).Count;
            entry.SoulId = soulMap.TryGetValue(entry.UserId, out var s) ? s : "default";
        }

        return (msgStats, total);
    }

    public NyaUserOverview GetUserOverview(string userId)
    {
        var totalMsgs   = ReadScalar<int>("SELECT COUNT(*) FROM nya_messages WHERE user_id = @userId AND is_deleted = 0", new { userId });
        var summarized  = ReadScalar<int>("SELECT COUNT(*) FROM nya_messages WHERE user_id = @userId AND is_deleted = 0 AND is_summarized = 1", new { userId });
        var deleted     = ReadScalar<int>("SELECT COUNT(*) FROM nya_messages WHERE user_id = @userId AND is_deleted = 1", new { userId });
        var lastMsgAt   = ReadScalar<DateTime?>("SELECT MAX(created_at) FROM nya_messages WHERE user_id = @userId AND is_deleted = 0", new { userId });
        var lastFactAt  = ReadScalar<DateTime?>("SELECT MAX(created_at) FROM nya_notes WHERE user_id = @userId AND is_current = 1", new { userId });

        var activeFacts     = ListNotes(userId);
        var allSummaries    = GetSummaryHistory(userId);

        var activeSummaries = GetActiveSummaries(userId);
        var soulId          = GetSoulBinding(userId);

        var catCounts = activeFacts
            .GroupBy(f => f.Scope)
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
            Notes = new NyaNoteStats
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
            LastNoteUpdatedAt = lastFactAt
        };
    }

    public NyaMessage? GetMessage(string userId, string messageId)
        => ReadFirst<NyaMessage>(
            "SELECT * FROM nya_messages WHERE message_id = @messageId AND user_id = @userId",
            new { messageId, userId });

    private long GetNextSequence(string userId)
    {
        var maxSeq = ReadScalar<long?>(
            "SELECT MAX(sequence) FROM nya_messages WHERE user_id = @userId",
            new { userId });
        return (maxSeq ?? 0) + 1;
    }

    public bool RemoveSoulBinding(string userId)
        => _db.Execute("DELETE FROM nya_soul_bindings WHERE user_id = @userId", new { userId }) > 0;

}
