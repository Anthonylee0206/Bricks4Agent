using System.Text;
using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

public partial class NyaMemoryService
{
    public NyaToolResult PageMemory(NyaToolContext context, string queryKey, string? cursor, string[] initialIds, int limit, Func<string, object?> project)
    {
        return _db.InTransaction(() =>
        {
            var offset = 0;
            var ids = initialIds;
            if (cursor != null)
            {
                var saved = ReadFirst<NyaHistoryCursor>("SELECT * FROM nya_history_cursors WHERE cursor_id = @cursor AND user_id = @userId", new { cursor, userId = context.UserId });
                if (saved == null || !SameScope(context.Scope, context.ExternalConversationId, saved.Scope, saved.ConversationId) || saved.QueryKey != queryKey)
                    throw new NyaMemoryException("cursor_mismatch", "cursor", "cursor 不屬於此查詢或範圍。");
                if (saved.ExpiresAt <= DateTime.UtcNow || saved.VisibilityVersion != VisibilityVersion(context.UserId))
                    throw new NyaMemoryException("cursor_expired", "cursor", "cursor 已過期，請重新查詢。");
                ids = Ids(saved.IdsJson);
                offset = saved.Offset;
            }
            var page = ids.Skip(offset).Take(limit).Select(project).Where(x => x != null).ToArray();
            var nextOffset = Math.Min(ids.Length, offset + limit);
            string? nextCursor = null;
            if (nextOffset < ids.Length)
            {
                nextCursor = MemoryId("nyac_");
                _db.Insert(new NyaHistoryCursor
                {
                    CursorId = nextCursor, UserId = context.UserId, Scope = context.Scope, ConversationId = context.ExternalConversationId,
                    QueryKey = queryKey, VisibilityVersion = VisibilityVersion(context.UserId), IdsJson = JsonSerializer.Serialize(ids),
                    Offset = nextOffset, ExpiresAt = DateTime.UtcNow.AddMinutes(15)
                });
            }
            return NyaToolResult.Envelope(new { results = page, cursor = nextCursor, eof = nextCursor == null, total = ids.Length });
        });
    }

    public NyaToolResult SearchHistory(NyaToolContext context, string query, string match, string? topic, string? from, string? to, int limit, string? cursor)
    {
        var topicId = topic == "current" ? context.ConversationId : topic;
        if (context.Scope == "external" && topicId != null && topicId != context.ExternalConversationId)
            throw new NyaMemoryException("scope_denied", "topic", "不能讀取此範圍以外的聊天室。");
        if (context.Scope == "normal" && topicId != null && !IsUserTopic(context.UserId, topicId))
            throw new NyaMemoryException("topic_not_found", "topic", "找不到此範圍的聊天室。");
        DateTime? ParseDate(string? date, string field)
        {
            if (date == null) return null;
            if (!DateTime.TryParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
                throw new NyaMemoryException("invalid_date", field, "預期有效 yyyy-MM-dd 台灣日期。");
            return DateTime.SpecifyKind(day.AddHours(-8), DateTimeKind.Utc);
        }
        var start = ParseDate(from, "from_date");
        var end = ParseDate(to, "to_date")?.AddDays(1);
        if (start > end) throw new NyaMemoryException("date_range", "to_date", "結束日期不可早於開始日期。");
        var terms = query.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var parameters = new Dictionary<string, object?> { ["userId"] = context.UserId, ["scope"] = context.Scope,
            ["conversationId"] = context.ExternalConversationId, ["topicId"] = topicId, ["excludeTurnId"] = null, ["start"] = start, ["end"] = end };
        var predicates = terms.Select((term, i) =>
        {
            parameters["term" + i] = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            return "m.content LIKE @term" + i + " ESCAPE '\\'";
        }).ToArray();
        var messages = cursor != null ? [] : ReadQuery<NyaMessage>("SELECT m.* FROM nya_messages m WHERE " + VisibleMessagePredicate +
            " AND (@start IS NULL OR m.created_at >= @start) AND (@end IS NULL OR m.created_at < @end) AND (" +
            (predicates.Length == 0 ? "0 = 1" : string.Join(match == "all" ? " AND " : " OR ", predicates)) + ") ORDER BY m.sequence DESC", parameters);
        var topics = context.Scope == "normal" ? ListTopicsWithCount(context.UserId) : [];
        var queryKey = JsonSerializer.Serialize(new { tool = "search_history", query, match, topicId, from, to });
        return PageMemory(context, queryKey, cursor, messages.Select(m => m.MessageId).ToArray(), limit, id =>
        {
            var message = VisibleMessage(context, id);
            if (message == null) return null;
            var positions = terms.Select(t => message.Content.IndexOf(t, StringComparison.OrdinalIgnoreCase)).Where(p => p >= 0).ToArray();
            var pos = positions.Length == 0 ? 0 : positions.Min();
            var fragmentStart = Math.Max(0, pos - 80);
            if (fragmentStart > 0 && char.IsLowSurrogate(message.Content[fragmentStart]) && char.IsHighSurrogate(message.Content[fragmentStart - 1])) fragmentStart--;
            var fragmentEnd = Math.Min(message.Content.Length, fragmentStart + 240);
            if (fragmentEnd < message.Content.Length && fragmentEnd > fragmentStart && char.IsHighSurrogate(message.Content[fragmentEnd - 1]) && char.IsLowSurrogate(message.Content[fragmentEnd])) fragmentEnd--;
            return new
            {
                message_id = id, topic_id = message.TopicId, topic_title = topics.FirstOrDefault(t => t.TopicId == message.TopicId)?.Title,
                date = message.CreatedAt, speaker = message.Role, snippet = message.Content.Substring(fragmentStart, fragmentEnd - fragmentStart),
                snippet_offset = fragmentStart, in_current_context = context.CurrentContextMessageIds.Contains(id)
            };
        });
    }

    public NyaToolResult ReadHistory(NyaToolContext context, string messageId, int before, int after, int segmentLength, string? cursor)
    {
        var target = VisibleMessage(context, messageId)
            ?? throw new NyaMemoryException("message_not_found", "message_id", "找不到可見訊息。");
        var parameters = new { userId = context.UserId, scope = context.Scope, conversationId = context.ExternalConversationId,
            topicId = target.TopicId, excludeTurnId = (string?)null, sequence = target.Sequence, before, after };
        var earlier = before == 0 || cursor != null ? [] : ReadQuery<NyaMessage>("SELECT m.* FROM nya_messages m WHERE " + VisibleMessagePredicate +
            " AND m.sequence < @sequence ORDER BY m.sequence DESC LIMIT @before", parameters);
        var later = after == 0 || cursor != null ? [] : ReadQuery<NyaMessage>("SELECT m.* FROM nya_messages m WHERE " + VisibleMessagePredicate +
            " AND m.sequence > @sequence ORDER BY m.sequence LIMIT @after", parameters);
        var selected = earlier.AsEnumerable().Reverse().Concat(new[] { target }).Concat(later).ToList();
        var segments = selected.SelectMany(m => SegmentOffsets(m, segmentLength)).ToArray();
        var queryKey = JsonSerializer.Serialize(new { tool = "read_history", messageId, before, after, segmentLength });
        return PageMemory(context, queryKey, cursor, segments, 1, key =>
        {
            var parts = key.Split(':');
            var message = VisibleMessage(context, parts[0]);
            if (message == null) return null;
            var offset = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            var length = SegmentLength(message.Content, offset, segmentLength);
            return new
            {
                message_id = message.MessageId, topic_id = message.TopicId, date = message.CreatedAt, speaker = message.Role,
                offset, text = message.Content.Substring(offset, length), message_eof = offset + length >= message.Content.Length,
                in_current_context = context.CurrentContextMessageIds.Contains(message.MessageId)
            };
        });
    }

    public NyaMessage? VisibleMessage(NyaToolContext context, string messageId)
        => ReadFirst<NyaMessage>("SELECT m.* FROM nya_messages m WHERE " + VisibleMessagePredicate + " AND m.message_id = @messageId",
            new { userId = context.UserId, scope = context.Scope, conversationId = context.ExternalConversationId,
                topicId = (string?)null, excludeTurnId = (string?)null, messageId });

    private static IEnumerable<string> SegmentOffsets(NyaMessage message, int segmentLength)
    {
        var offset = 0;
        do
        {
            yield return message.MessageId + ":" + offset;
            offset += SegmentLength(message.Content, offset, segmentLength);
        } while (offset < message.Content.Length);
    }

    private static int SegmentLength(string content, int offset, int limit)
    {
        var length = 0;
        var count = 0;
        foreach (var rune in content.AsSpan(offset).EnumerateRunes())
        {
            if (count++ >= limit) break;
            length += rune.Utf16SequenceLength;
        }
        return length;
    }
}
