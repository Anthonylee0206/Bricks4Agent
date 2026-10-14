using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

public partial class NyaMemoryService
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _userGates = new(StringComparer.Ordinal);

    public async Task<IDisposable> LockUserAsync(string userId, CancellationToken ct = default)
    {
        var gate = _userGates.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        return new UserLease(gate);
    }

    private sealed class UserLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private void InitializeTurnMemory()
    {
        _db.EnsureTable<NyaTurn>();
        _db.EnsureTable<NyaNote>();
        _db.EnsureTable<NyaHideReference>();
        _db.EnsureTable<NyaMemorySelection>();
        _db.EnsureTable<NyaHistoryCursor>();
        _db.EnsureTable<NyaTurnWork>();
        EnsureColumn("nya_notes", "topic_id", "TEXT");
        EnsureColumn("nya_summaries", "source_message_ids", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn("nya_summaries", "visibility_version", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("nya_summaries", "scope", "TEXT NOT NULL DEFAULT 'normal'");
        EnsureColumn("nya_summaries", "conversation_id", "TEXT");
        EnsureColumn("nya_messages", "turn_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("nya_messages", "origin_topic_id", "TEXT");
        EnsureColumn("nya_messages", "scope", "TEXT NOT NULL DEFAULT 'normal'");
        EnsureColumn("nya_messages", "conversation_id", "TEXT");
        _db.Execute("UPDATE nya_turns SET status = 'failed', failure_reason = 'broker_restarted', finished_at = @now WHERE status = 'pending'", new { now = DateTime.UtcNow });
    }

    private static string MemoryId(string prefix) => prefix + Guid.NewGuid().ToString("N");
    private static string[] Ids(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];
    private static bool SameScope(string scope, string? conversationId, string otherScope, string? otherConversation)
        => scope == otherScope && (scope == "normal" || conversationId == otherConversation);

    private void RecordTurnEffect(string userId, string turnId, object effect)
    {
        if (turnId.Length == 0 || GetTurn(userId, turnId) == null) return;
        _db.Insert(new NyaTurnWork { WorkId = MemoryId("nyaw_"), TurnId = turnId, Position = GetTurnWork(turnId).Count,
            EffectsJson = JsonSerializer.Serialize(new[] { effect }) });
    }

    public NyaTurn BeginTurn(string userId, string turnId, string topicId, string message, string? externalConversationId)
    {
        return _db.InTransaction(() =>
        {
            var turn = new NyaTurn
            {
                TurnId = turnId, UserId = userId, StartTopicId = topicId, CurrentTopicId = topicId,
                Scope = externalConversationId == null ? "normal" : "external", ConversationId = externalConversationId
            };
            _db.Insert(turn);
            InsertTurnMessage(turn, "user", message);
            return turn;
        });
    }

    private NyaMessage InsertTurnMessage(NyaTurn turn, string role, string content)
    {
        var message = new NyaMessage
        {
            MessageId = MemoryId("nyam_"), UserId = turn.UserId, Role = role, Content = content,
            Sequence = GetNextSequence(turn.UserId), TopicId = turn.CurrentTopicId, OriginTopicId = turn.StartTopicId,
            TurnId = turn.TurnId, Scope = turn.Scope, ConversationId = turn.ConversationId,
            MetadataJson = JsonSerializer.Serialize(new { trace_id = turn.TurnId })
        };
        _db.Insert(message);
        return message;
    }

    public NyaTurn? GetTurn(string userId, string turnId)
        => ReadFirst<NyaTurn>("SELECT * FROM nya_turns WHERE user_id = @userId AND turn_id = @turnId", new { userId, turnId });

    public List<NyaTurn> ListTurns(string userId, int limit = 100, int offset = 0)
        => ReadQuery<NyaTurn>("SELECT * FROM nya_turns WHERE user_id = @userId ORDER BY created_at DESC, turn_id LIMIT @limit OFFSET @offset", new { userId, limit, offset });

    public void FinishTurn(NyaTurn turn, string? reply, string? failure = null)
    {
        _db.InTransaction(() =>
        {
            if (reply != null) InsertTurnMessage(turn, "assistant", reply);
            _db.Execute("UPDATE nya_turns SET status = @status, failure_reason = @failure, finished_at = @now WHERE turn_id = @id AND status = 'pending'",
                new { status = reply == null ? "failed" : "completed", failure, now = DateTime.UtcNow, id = turn.TurnId });
        });
        turn.Status = reply == null ? "failed" : "completed";
        turn.FailureReason = failure;
    }

    public NyaTopic ChangeTurnTopic(NyaTurn turn, string? targetId, string? title = null)
    {
        if (turn.Scope != "normal") throw new NyaMemoryException("external_scope", "topic", "外部明確會話不能操作聊天室。");
        var topic = _db.InTransaction(() =>
        {
            NyaTopic target;
            if (targetId == null)
            {
                if (ListTopicsWithCount(turn.UserId).Count >= _config.TopicCapacity)
                    throw new NyaMemoryException("topic_capacity", "title", "聊天室已滿，請先整理。");
                target = CreateNewTopicCore(turn.UserId, title);
            }
            else
            {
                target = ReadFirst<NyaTopic>("SELECT * FROM nya_topics WHERE user_id = @userId AND topic_id = @targetId", new { userId = turn.UserId, targetId })
                    ?? throw new NyaMemoryException("topic_not_found", "topic", "找不到聊天室。");
                _db.Execute("UPDATE nya_topics SET is_active = 0 WHERE user_id = @userId", new { userId = turn.UserId });
                _db.Execute("UPDATE nya_topics SET is_active = 1 WHERE topic_id = @targetId", new { targetId });
                target.IsActive = true;
            }
            _db.Execute("UPDATE nya_turns SET current_topic_id = @targetId WHERE turn_id = @turnId", new { targetId = target.TopicId, turnId = turn.TurnId });
            _db.Execute("UPDATE nya_messages SET topic_id = @targetId WHERE turn_id = @turnId AND role = 'user'", new { targetId = target.TopicId, turnId = turn.TurnId });
            _db.Insert(new NyaTurnWork
            {
                WorkId = MemoryId("nyaw_"), TurnId = turn.TurnId, Position = GetTurnWork(turn.TurnId).Count,
                EffectsJson = JsonSerializer.Serialize(new[] { new { action = targetId == null ? "new_topic" : "switch_topic", origin_topic_id = turn.CurrentTopicId, topic_id = target.TopicId } })
            });
            return target;
        });
        turn.CurrentTopicId = topic.TopicId;
        return topic;
    }

    public List<NyaTopicEntry> FindTopics(string userId, string key)
    {
        var topics = ListTopicsWithCount(userId);
        var exact = topics.Where(t => t.TopicId == key).ToList();
        return exact.Count > 0 ? exact : topics.Where(t => t.Title == key).ToList();
    }

    public List<NyaTurnWork> GetTurnWork(string turnId)
    {
        return ReadQuery<NyaTurnWork>(@"SELECT w.*, CASE WHEN EXISTS (
            SELECT 1 FROM nya_hide_refs h JOIN nya_turns t ON t.turn_id = w.turn_id
            WHERE h.user_id = t.user_id AND h.is_active = 1 AND (
                (h.kind = 'work' AND h.target_id = w.work_id) OR
                (h.kind IN ('message', 'note') AND h.target_id IN (SELECT value FROM json_each(w.dependencies))) OR
                (h.kind = 'turn' AND h.target_id IN (SELECT m.turn_id FROM nya_messages m
                    WHERE m.message_id IN (SELECT value FROM json_each(w.dependencies)))))) THEN 1 ELSE 0 END AS is_hidden
            FROM nya_turn_work w WHERE w.turn_id = @turnId ORDER BY w.position", new { turnId });
    }

    public void SaveTurnWork(NyaTurn turn, NyaToolCall call, NyaToolResult result, IEnumerable<string> dependencies)
    {
        var ids = dependencies.Distinct().ToArray();
        _db.Insert(new NyaTurnWork
        {
            WorkId = MemoryId("nyaw_"), TurnId = turn.TurnId, Position = GetTurnWork(turn.TurnId).Count,
            CallJson = JsonSerializer.Serialize(call), ResultJson = WithoutContextFlags(result.LlmContent),
            EffectsJson = JsonSerializer.Serialize(result.Effects), DependenciesJson = JsonSerializer.Serialize(ids),
            IsHidden = false
        });
    }

    private static string WithoutContextFlags(string json)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json);
        void Walk(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                if (obj["message_id"] != null && new[] { "text", "snippet", "preview", "content" }.Any(key => obj[key] != null)) obj.Remove("in_current_context");
                foreach (var property in obj) Walk(property.Value);
            }
            else if (node is System.Text.Json.Nodes.JsonArray array) foreach (var item in array) Walk(item);
        }
        Walk(root);
        return root?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? json;
    }

    public List<NyaHideReference> GetHideReferences(string userId)
        => ReadQuery<NyaHideReference>("SELECT * FROM nya_hide_refs WHERE user_id = @userId ORDER BY created_at DESC", new { userId });

    public long VisibilityVersion(string userId)
        => ReadScalar<long>("SELECT (SELECT COALESCE(SUM(CASE WHEN is_active = 1 THEN 1 ELSE 2 END), 0) FROM nya_hide_refs WHERE user_id = @userId) + (SELECT COUNT(*) FROM nya_notes WHERE user_id = @userId) + (SELECT COUNT(*) FROM nya_messages WHERE user_id = @userId AND is_deleted = 1)", new { userId });

    public bool IsHidden(string userId, string kind, string targetId)
        => ReadScalar<int>(@"SELECT COUNT(*) FROM nya_hide_refs h WHERE h.user_id = @userId AND h.is_active = 1
            AND ((h.kind = @kind AND h.target_id = @targetId) OR (@kind = 'message' AND h.kind = 'turn'
                AND EXISTS (SELECT 1 FROM nya_messages m WHERE m.user_id = @userId AND m.message_id = @targetId AND m.turn_id = h.target_id)))",
            new { userId, kind, targetId }) > 0;

    public List<NyaNote> ListNotes(string userId, string scope = "normal", string? conversationId = null, bool activeOnly = true)
        => ReadQuery<NyaNote>(@"SELECT n.* FROM nya_notes n WHERE n.user_id = @userId AND n.scope = @scope
            AND (@scope = 'normal' OR n.conversation_id = @conversationId)
            AND (@active = 0 OR (n.is_current = 1 AND (n.expires_at IS NULL OR n.expires_at > @now)))
            AND NOT EXISTS (SELECT 1 FROM nya_hide_refs h WHERE h.user_id = n.user_id AND h.is_active = 1
                AND ((h.kind = 'note' AND h.target_id = n.note_id) OR (h.kind = 'note_version' AND h.target_id = n.version_id)))
            ORDER BY n.note_key, n.version DESC", new { userId, scope, conversationId, active = activeOnly ? 1 : 0, now = DateTime.UtcNow });

    public List<NyaNote> AdminNotes(string userId)
        => ReadQuery<NyaNote>("SELECT * FROM nya_notes WHERE user_id = @userId ORDER BY created_at DESC", new { userId });

    public static object ModelNote(NyaNote note) => new
    {
        note_id = note.NoteId, key = note.Key, version = note.Version, content = note.Content, expires_at = note.ExpiresAt,
        source_message_ids = Ids(note.SourceMessageIdsJson)
    };

    public static int NoteSnapshotTokens(IEnumerable<NyaNote> notes, Func<string, int>? estimate = null)
    {
        var text = JsonSerializer.Serialize(notes.Select(ModelNote), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return estimate?.Invoke(text) ?? NyaTokenEstimator.Default.Estimate(text);
    }

    public List<NyaMemorySelection> AdminSelections(string userId, int limit = 100, int offset = 0)
        => ReadQuery<NyaMemorySelection>("SELECT * FROM nya_memory_selections WHERE user_id = @userId ORDER BY expires_at DESC LIMIT @limit OFFSET @offset", new { userId, limit, offset });

    public int ClearTurnMemory(string userId)
    {
        return _db.InTransaction(() =>
        {
            _db.Execute("DELETE FROM nya_turn_work WHERE turn_id IN (SELECT turn_id FROM nya_turns WHERE user_id = @userId)", new { userId });
            _db.Execute("DELETE FROM nya_turns WHERE user_id = @userId", new { userId });
            _db.Execute("DELETE FROM nya_memory_selections WHERE user_id = @userId", new { userId });
            _db.Execute("DELETE FROM nya_history_cursors WHERE user_id = @userId", new { userId });
            _db.Execute("DELETE FROM nya_hide_refs WHERE user_id = @userId", new { userId });
            return _db.Execute("DELETE FROM nya_notes WHERE user_id = @userId", new { userId });
        });
    }

    public NyaNote RestoreNote(NyaToolContext context, string noteId, int version)
    {
        var target = AdminNotes(context.UserId).FirstOrDefault(n => n.NoteId == noteId && n.Version == version && SameScope(context.Scope, context.ExternalConversationId, n.Scope, n.ConversationId))
            ?? throw new NyaMemoryException("note_not_found", "note_id", "找不到指定版本。");
        if (IsHidden(context.UserId, "note", noteId) || IsHidden(context.UserId, "note_version", target.VersionId))
            throw new NyaMemoryException("note_hidden", "note_id", "請先撤銷指定隱藏引用，才能還原筆記。");
        if (ListNotes(context.UserId, context.Scope, context.ExternalConversationId, false).Any(n => n.IsCurrent && n.Key == target.Key && n.NoteId != noteId))
            throw new NyaMemoryException("key_conflict", "note_id", "筆記 key 衝突，拒絕還原。");
        return SaveNote(context, noteId, null, target.Content, target.ExpiresAt == null ? "never" : "on_date",
            target.ExpiresAt?.AddHours(-16).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Ids(target.SourceMessageIdsJson));
    }

    public NyaNote SaveNote(NyaToolContext context, string? noteId, string? key, string content, string expiry, string? date, string[] sourceIds)
    {
        return _db.InTransaction(() =>
        {
            var all = AdminNotes(context.UserId).Where(n => SameScope(context.Scope, context.ExternalConversationId, n.Scope, n.ConversationId)).ToList();
            var existing = noteId == null ? null : all.Where(n => n.NoteId == noteId).OrderByDescending(n => n.Version).FirstOrDefault();
            if (noteId != null && (existing == null || IsHidden(context.UserId, "note", noteId)))
                throw new NyaMemoryException("note_not_found", "note_id", "找不到可見的筆記。");
            key = existing?.Key ?? key;
            if (string.IsNullOrWhiteSpace(key)) throw new NyaMemoryException("required", "key", "新增筆記需要 key。");
            if (ListNotes(context.UserId, context.Scope, context.ExternalConversationId, false).Any(n => n.IsCurrent && n.Key == key && n.NoteId != noteId))
                throw new NyaMemoryException("key_conflict", "key", "key 已存在，請以 note_id 更正。");
            DateTime? expires = expiry switch
            {
                "keep" => existing?.ExpiresAt,
                "never" => null,
                "on_date" => DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                    ? DateTime.SpecifyKind(parsed.AddHours(16), DateTimeKind.Utc)
                    : throw new NyaMemoryException("invalid_date", "expiry.date", "日期必須是有效的 yyyy-MM-dd 台灣日期。"),
                _ => throw new NyaMemoryException("enum", "expiry.mode", "期限必須是 keep、never 或 on_date。")
            };
            ValidateNoteCapacity(context.UserId, context.Scope, context.ExternalConversationId, content, expires, existing?.NoteId);
            foreach (var id in sourceIds)
            {
                var message = GetMessage(context.UserId, id);
                if (message == null || !SameScope(context.Scope, context.ExternalConversationId, message.Scope, message.ConversationId) || IsHidden(context.UserId, "message", id))
                    throw new NyaMemoryException("source_not_found", "source_message_ids", "來源訊息不在可見範圍內。");
            }
            var note = new NyaNote
            {
                VersionId = MemoryId("nyav_"), NoteId = existing?.NoteId ?? MemoryId("nyan_"), UserId = context.UserId,
                Scope = context.Scope, ConversationId = context.ExternalConversationId, Key = key, Content = content,
                TopicId = existing?.TopicId ?? context.ConversationId,
                Version = (existing?.Version ?? 0) + 1, IsCurrent = true, ExpiresAt = expires,
                SourceMessageIdsJson = JsonSerializer.Serialize(sourceIds)
            };
            if (context.NoteTokenBudget.HasValue)
            {
                var snapshot = ListNotes(context.UserId, context.Scope, context.ExternalConversationId).Where(n => n.NoteId != note.NoteId).ToList();
                if (note.ExpiresAt == null || note.ExpiresAt > DateTime.UtcNow) snapshot.Add(note);
                if (NoteSnapshotTokens(snapshot, context.EstimateTokens) > context.NoteTokenBudget.Value)
                    throw new NyaMemoryException("note_capacity", "content", "筆記已滿，請先整理");
            }
            _db.Execute("UPDATE nya_notes SET is_current = 0 WHERE note_id = @id", new { id = note.NoteId });
            _db.Insert(note);
            RecordTurnEffect(context.UserId, context.TraceId, new { action = "note_save", note_id = note.NoteId, version = note.Version });
            return note;
        });
    }

    private void ValidateNoteCapacity(string userId, string scope, string? conversationId, string content, DateTime? expiresAt, string? replacing)
    {
        var active = ListNotes(userId, scope, conversationId).Where(n => n.NoteId != replacing).ToList();
        if (content.EnumerateRunes().Count() > _config.NoteMaxLength || (expiresAt == null || expiresAt > DateTime.UtcNow) && active.Count >= _config.NoteCapacity)
            throw new NyaMemoryException("note_capacity", "content", "筆記已滿，請先整理");
    }

    private const string VisibleMessagePredicate = @"m.user_id = @userId AND m.is_deleted = 0 AND m.scope = @scope
        AND (@scope = 'normal' OR m.conversation_id = @conversationId)
        AND (@topicId IS NULL OR m.topic_id = @topicId) AND (@excludeTurnId IS NULL OR m.turn_id <> @excludeTurnId)
        AND NOT EXISTS (SELECT 1 FROM nya_hide_refs h WHERE h.user_id = m.user_id AND h.is_active = 1
            AND ((h.kind = 'message' AND h.target_id = m.message_id) OR (h.kind = 'turn' AND h.target_id = m.turn_id)))";

    public List<NyaMessage> VisibleMessages(NyaToolContext context, string? topicId = null, string? excludeTurnId = null)
        => ReadQuery<NyaMessage>("SELECT m.* FROM nya_messages m WHERE " + VisibleMessagePredicate + " ORDER BY m.sequence",
            new { userId = context.UserId, scope = context.Scope, conversationId = context.ExternalConversationId, topicId, excludeTurnId });

    public List<NyaMessage> SelectRecentHistory(NyaToolContext context, string topicId, string? excludeTurnId = null,
        Func<NyaMessage, bool>? accept = null)
    {
        var selected = new List<NyaMessage>();
        var before = long.MaxValue;
        var pageSize = Math.Min(32, _config.ShortTermMessageLimit);
        while (selected.Count < _config.ShortTermMessageLimit)
        {
            var page = ReadQuery<NyaHistoryMessage>("SELECT m.*, t.status AS turn_status FROM nya_messages m LEFT JOIN nya_turns t ON t.turn_id = m.turn_id WHERE " + VisibleMessagePredicate +
                " AND (@summaries = 0 OR m.is_summarized = 0) AND m.sequence < @before ORDER BY m.sequence DESC LIMIT @limit",
                new { userId = context.UserId, scope = context.Scope, conversationId = context.ExternalConversationId, topicId,
                    excludeTurnId, summaries = _config.AutoSummarizeEnabled ? 1 : 0, before, limit = Math.Min(pageSize, _config.ShortTermMessageLimit - selected.Count) });
            if (page.Count == 0) break;
            foreach (var message in page)
            {
                if (accept != null && !accept(message)) { selected.Reverse(); return selected; }
                selected.Add(message);
            }
            before = page[^1].Sequence;
            if (page.Count < pageSize) break;
        }
        selected.Reverse();
        return selected;
    }

    public NyaMessage? VisibleTurnUser(NyaToolContext context, string turnId)
        => ReadFirst<NyaMessage>("SELECT m.* FROM nya_messages m WHERE " + VisibleMessagePredicate + " AND m.turn_id = @turnId AND m.role = 'user' LIMIT 1",
            new { userId = context.UserId, scope = context.Scope, conversationId = context.ExternalConversationId, topicId = (string?)null, excludeTurnId = (string?)null, turnId });

    public NyaToolResult FindForget(NyaToolContext context, string[] terms, string[] noteIds, string match, bool currentOnly = false)
    {
        return _db.InTransaction(() =>
        {
            bool Matches(string text) => terms.Length > 0 && (match == "all" ? terms.All(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)) : terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)));
            var visibleNotes = ListNotes(context.UserId, context.Scope, context.ExternalConversationId, false)
                .Where(n => !currentOnly || n.TopicId == context.ConversationId).ToList();
            if (noteIds.Any(id => !visibleNotes.Any(n => n.NoteId == id))) throw new NyaMemoryException("note_not_found", "note_ids", "筆記 ID 不在可見範圍內。");
            var notes = visibleNotes.Where(n => noteIds.Contains(n.NoteId) || Matches(n.Key + " " + n.Content)).ToList();
            var messages = VisibleMessages(context, currentOnly ? context.ConversationId : null);
            var sources = notes.SelectMany(n => Ids(n.SourceMessageIdsJson)).ToHashSet();
            var selected = messages.Where(m => Matches(m.Content) || sources.Contains(m.MessageId)).ToList();
            var selectedTurns = selected.Where(m => m.TurnId.Length > 0).Select(m => m.TurnId).ToHashSet();
            selected = messages.Where(m => selected.Any(s => s.MessageId == m.MessageId) || selectedTurns.Contains(m.TurnId)).ToList();
            var selection = new NyaMemorySelection
            {
                SelectionId = MemoryId("nyas_"), UserId = context.UserId, Scope = context.Scope, ConversationId = context.ExternalConversationId,
                VisibilityVersion = VisibilityVersion(context.UserId), ExpiresAt = DateTime.UtcNow.AddMinutes(15),
                NoteIdsJson = JsonSerializer.Serialize(notes.Select(n => n.NoteId).Distinct()), MessageIdsJson = JsonSerializer.Serialize(selected.Select(m => m.MessageId))
            };
            _db.Insert(selection);
            return NyaToolResult.Envelope(new
            {
                selection_id = selection.SelectionId, expires_at = selection.ExpiresAt,
                notes = notes.Select(n => new { note_id = n.NoteId, version = n.Version, preview = n.Content, source_message_ids = Ids(n.SourceMessageIdsJson) }),
                messages = selected.Select(m => new { message_id = m.MessageId, topic_id = m.TopicId, role = m.Role, preview = m.Content.Length > 240 ? m.Content[..240] : m.Content }),
                rule = "有歧義時先問使用者；不得替使用者自填確認。"
            }, "needs_confirmation");
        });
    }

    public NyaToolResult Forget(NyaToolContext context, string? selectionId, string[] noteIds, string[] messageIds)
    {
        return _db.InTransaction(() =>
        {
            if (selectionId != null)
            {
                var selection = ReadFirst<NyaMemorySelection>("SELECT * FROM nya_memory_selections WHERE selection_id = @selectionId AND user_id = @userId", new { selectionId, userId = context.UserId });
                if (selection == null || !SameScope(context.Scope, context.ExternalConversationId, selection.Scope, selection.ConversationId))
                    throw new NyaMemoryException("selection_not_found", "selection_id", "候選不在此範圍內。");
                if (selection.ExpiresAt <= DateTime.UtcNow || selection.VisibilityVersion != VisibilityVersion(context.UserId))
                    throw new NyaMemoryException("selection_expired", "selection_id", "候選已過期，請重新查找。");
                noteIds = Ids(selection.NoteIdsJson);
                messageIds = Ids(selection.MessageIdsJson);
            }
            var allNotes = AdminNotes(context.UserId).Where(n => SameScope(context.Scope, context.ExternalConversationId, n.Scope, n.ConversationId)).ToList();
            var allMessages = ReadQuery<NyaMessage>("SELECT * FROM nya_messages WHERE user_id = @userId", new { userId = context.UserId })
                .Where(m => SameScope(context.Scope, context.ExternalConversationId, m.Scope, m.ConversationId)).ToList();
            if (noteIds.Any(id => !allNotes.Any(n => n.NoteId == id))) throw new NyaMemoryException("note_not_found", "note_ids", "筆記 ID 不在此範圍內。");
            if (messageIds.Any(id => !allMessages.Any(m => m.MessageId == id))) throw new NyaMemoryException("message_not_found", "message_ids", "訊息 ID 不在此範圍內。");
            var sourceIds = allNotes.Where(n => noteIds.Contains(n.NoteId)).SelectMany(n => Ids(n.SourceMessageIdsJson));
            var chosen = messageIds.Concat(sourceIds).ToHashSet();
            if (context.TraceId.Length > 0 && chosen.Count + noteIds.Length > 0)
                chosen.UnionWith(allMessages.Where(m => m.TurnId == context.TraceId).Select(m => m.MessageId));
            var turns = allMessages.Where(m => chosen.Contains(m.MessageId) && m.TurnId.Length > 0).Select(m => m.TurnId).ToHashSet();
            chosen.UnionWith(allMessages.Where(m => turns.Contains(m.TurnId)).Select(m => m.MessageId));
            var forgetId = MemoryId("nyag_");
            foreach (var (kind, ids) in new[] { ("note", noteIds.Distinct()), ("message", chosen.AsEnumerable()), ("turn", turns.AsEnumerable()) })
                foreach (var id in ids)
                    _db.Insert(new NyaHideReference { ReferenceId = MemoryId("nyar_"), ForgetId = forgetId, UserId = context.UserId, Scope = context.Scope, ConversationId = context.ExternalConversationId, Kind = kind, TargetId = id });
            foreach (var work in ReadQuery<NyaTurnWork>("SELECT w.* FROM nya_turn_work w JOIN nya_turns t ON t.turn_id = w.turn_id WHERE t.user_id = @userId", new { userId = context.UserId }))
                if (turns.Contains(work.TurnId) || Ids(work.DependenciesJson).Any(id => chosen.Contains(id) || noteIds.Contains(id)))
                    _db.Insert(new NyaHideReference { ReferenceId = MemoryId("nyar_"), ForgetId = forgetId, UserId = context.UserId, Scope = context.Scope,
                        ConversationId = context.ExternalConversationId, Kind = "work", TargetId = work.WorkId });
            foreach (var summary in GetSummaryHistory(context.UserId))
                if (Ids(summary.SourceMessageIdsJson).Any(chosen.Contains) || allMessages.Any(m => chosen.Contains(m.MessageId) && m.TopicId == summary.TopicId && m.Sequence >= summary.CoveredFromSeq && m.Sequence <= summary.CoveredToSeq))
                {
                    _db.Execute("UPDATE nya_summaries SET is_active = 0 WHERE summary_id = @id", new { id = summary.SummaryId });
                    foreach (var sourceId in Ids(summary.SourceMessageIdsJson))
                        _db.Execute("UPDATE nya_messages SET is_summarized = 0 WHERE user_id = @userId AND message_id = @sourceId", new { userId = context.UserId, sourceId });
                }
            if (chosen.Count + noteIds.Length > 0)
                RecordTurnEffect(context.UserId, context.TraceId, new { action = "forget", forget_id = forgetId, note_count = noteIds.Length, message_count = chosen.Count });
            return NyaToolResult.Envelope(new { forget_id = forgetId, note_ids = noteIds.Distinct().ToArray(), message_ids = chosen.ToArray(), note_count = noteIds.Distinct().Count(), message_count = chosen.Count },
                chosen.Count + noteIds.Length == 0 ? "noop" : "completed",
                chosen.Count + noteIds.Length == 0 ? [] : new object[] { new { action = "forget", forget_id = forgetId, note_count = noteIds.Length, message_count = chosen.Count } });
        });
    }

    public bool Unforget(string userId, string forgetId, int? noteTokenBudget = null, Func<string, int>? estimate = null)
    {
        return _db.InTransaction(() =>
        {
            var refs = GetHideReferences(userId).Where(r => r.ForgetId == forgetId && r.IsActive).ToList();
            if (refs.Count == 0) return false;
            var restoring = new List<NyaNote>();
            foreach (var id in refs.Where(r => r.Kind == "note").Select(r => r.TargetId))
            {
                var note = AdminNotes(userId).FirstOrDefault(n => n.NoteId == id && n.IsCurrent);
                if (note == null || GetHideReferences(userId).Any(r => r.Kind == "note" && r.TargetId == id && r.IsActive && r.ForgetId != forgetId)) continue;
                if (ListNotes(userId, note.Scope, note.ConversationId, false).Any(n => n.IsCurrent && n.Key == note.Key && n.NoteId != id))
                    throw new NyaMemoryException("key_conflict", "forget_id", "還原筆記與現有 key 衝突。");
                ValidateNoteCapacity(userId, note.Scope, note.ConversationId, note.Content, note.ExpiresAt, id);
                restoring.Add(note);
            }
            foreach (var group in restoring.GroupBy(n => (n.Scope, n.ConversationId)))
            {
                var snapshot = ListNotes(userId, group.Key.Scope, group.Key.ConversationId)
                    .Concat(group.Where(n => n.ExpiresAt == null || n.ExpiresAt > DateTime.UtcNow)).DistinctBy(n => n.NoteId).ToList();
                if (snapshot.GroupBy(n => n.Key, StringComparer.Ordinal).Any(g => g.Count() > 1))
                    throw new NyaMemoryException("key_conflict", "forget_id", "還原筆記與現有 key 衝突。");
                if (snapshot.Count > _config.NoteCapacity || noteTokenBudget.HasValue && NoteSnapshotTokens(snapshot, estimate) > noteTokenBudget.Value)
                    throw new NyaMemoryException("note_capacity", "forget_id", "筆記已滿，請先整理");
            }
            _db.Execute("UPDATE nya_hide_refs SET is_active = 0 WHERE user_id = @userId AND forget_id = @forgetId", new { userId, forgetId });
            return true;
        });
    }
}
