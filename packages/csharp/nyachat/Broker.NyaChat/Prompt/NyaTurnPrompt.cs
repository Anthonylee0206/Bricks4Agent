using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

public partial class NyaPromptBuilder
{
    public List<NyaLlmMessage> BuildTurn(NyaTurn turn, NyaMemoryService memory, SoulDefinition soul, int remainingRounds,
        int inputBudget, NyaTokenEstimator estimator, string channelType, out HashSet<string> contextIds, out int noteBudget)
    {
        var context = new NyaToolContext { UserId = turn.UserId, Scope = turn.Scope, ExternalConversationId = turn.ConversationId, ConversationId = turn.CurrentTopicId };
        var summaries = _configStore.Current.AutoSummarizeEnabled ? memory.GetActiveSummaries(turn.UserId, turn.CurrentTopicId)
            .Where(s => s.Scope == turn.Scope && (turn.Scope == "normal" || s.ConversationId == turn.ConversationId)).ToList() : [];
        var stableSystem = BuildStableSystem(soul);
        var notes = memory.ListNotes(turn.UserId, turn.Scope, turn.ConversationId);
        var topics = turn.Scope == "normal" ? memory.ListTopicsWithCount(turn.UserId) : [];
        var directory = _toolRegistry.GetAllSchemas().OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => new
        {
            name = s.Name,
            availability = remainingRounds <= 0 ? "本輪工具回合已用完" : turn.Scope == "external" && s.Group == "topic" ? "外部明確會話不可用"
                : !_toolRegistry.IsEnabled(s) ? "停用" : !_toolRegistry.CanUse(turn.UserId, channelType, s) ? "未授權" : "可用"
        }).ToArray();
        object Overview(bool includeNotes) => new
        {
            tools = directory, remaining_tool_rounds = remainingRounds, scope = turn.Scope, conversation_id = turn.ConversationId,
            current_topic_id = turn.CurrentTopicId, topics,
            notes = includeNotes ? notes.Select(n => new { note_id = n.NoteId, key = n.Key, version = n.Version, content = n.Content, expires_at = n.ExpiresAt, source_message_ids = JsonSerializer.Deserialize<string[]>(n.SourceMessageIdsJson) }).ToArray() : null,
            capacity = new { used_notes = notes.Count, used_topics = topics.Count, notes = _configStore.Current.NoteCapacity, note_max_length = _configStore.Current.NoteMaxLength, topics = _configStore.Current.TopicCapacity },
            forgotten_count = memory.GetHideReferences(turn.UserId).Where(r => r.IsActive && r.Scope == turn.Scope && (turn.Scope == "normal" || r.ConversationId == turn.ConversationId)).Select(r => r.ForgetId).Distinct().Count(),
            hidden_reference_count = memory.GetHideReferences(turn.UserId).Count(r => r.IsActive && r.Scope == turn.Scope && (turn.Scope == "normal" || r.ConversationId == turn.ConversationId)),
            summaries = summaries.Select(s => new { summary_id = s.SummaryId, text = s.SummaryText }),
            current_datetime = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm")
        };
        var user = memory.VisibleTurnUser(context, turn.TurnId);
        var working = new List<NyaLlmMessage> { new() { Role = "user", Content = user?.Content ?? "[本輪使用者訊息已依遺忘要求隱藏]" } };
        foreach (var work in memory.GetTurnWork(turn.TurnId))
            AppendWork(working, work);
        var system = "<工作記憶與全貌>\n" + JsonSerializer.Serialize(Overview(true), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n</工作記憶與全貌>";
        var fixedCost = estimator.Estimate(stableSystem + JsonSerializer.Serialize(Overview(false), new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + ToolResultDirective()) + estimator.Estimate(working) + 64;
        noteBudget = Math.Max(0, inputBudget - fixedCost);
        var mandatory = new List<NyaLlmMessage> { new() { Role = "system", Content = stableSystem }, new() { Role = "system", Content = system } };
        mandatory.AddRange(working);
        if (estimator.Estimate(mandatory) > inputBudget)
            throw new NyaMemoryException("context_capacity", "context", "完整工具目錄、有效筆記與本輪工作紀錄超過上下文容量，請先整理或調大模型上下文。");
        var historyBudget = inputBudget - estimator.Estimate(mandatory);
        var selected = new List<NyaLlmMessage>();
        var selectedIds = new HashSet<string>();
        contextIds = selectedIds;
        (NyaLlmMessage Message, DateTime At)? newerUser = null;
        void LabelNewerUser(DateTime olderAt)
        {
            if (newerUser is not { } pending || pending.At - olderAt < HistoryTimeLabelGap) return;
            var labeled = $"[{pending.At.ToLocalTime():yyyy-MM-dd HH:mm}] {pending.Message.Content}";
            historyBudget -= estimator.Estimate(labeled) - estimator.Estimate(pending.Message.Content);
            pending.Message.Content = labeled;
        }
        memory.SelectRecentHistory(context, turn.CurrentTopicId, turn.TurnId, message =>
        {
            var at = HistoryUtc(message.CreatedAt);
            var spoken = new NyaLlmMessage { Role = message.Role, Content = message.Content };
            var entry = new List<NyaLlmMessage> { spoken };
            var oldTurn = message is NyaHistoryMessage { TurnStatus: "failed" } ? new NyaTurn { TurnId = message.TurnId, Status = "failed" } : null;
            if (message.Role == "user" && oldTurn?.Status == "failed")
            {
                var previousWork = memory.GetTurnWork(oldTurn.TurnId);
                var effects = previousWork.SelectMany(w => JsonSerializer.Deserialize<JsonElement>(w.EffectsJson).EnumerateArray())
                    .Where(e => !e.TryGetProperty("outcome", out var outcome) || outcome.GetString() == "completed")
                    .GroupBy(e => string.Join("|", new[] { "action", "topic_id", "note_id", "version", "forget_id" }.Select(key => e.TryGetProperty(key, out var value) ? value.ToString() : "")))
                    .Select(group => group.First().GetRawText()).ToArray();
                entry.Insert(0, new NyaLlmMessage { Role = "system", Content = $"[turn_id={oldTurn.TurnId}, message_id={message.MessageId}] 此回合未產生成功回覆；以下操作已完成：" + (effects.Length == 0 ? "無" : string.Join("；", effects)) + "。不要重做已完成的操作。" });
                foreach (var work in previousWork) AppendHistoricalWork(entry, work);
            }
            var cost = estimator.Estimate(entry);
            if (cost > historyBudget) return false;
            LabelNewerUser(at);
            selected.InsertRange(0, entry);
            selectedIds.Add(message.MessageId);
            historyBudget -= cost;
            newerUser = message.Role == "user" ? (spoken, at) : null;
            return true;
        });
        if (newerUser is { } oldest && DateTime.UtcNow - oldest.At >= HistoryTimeLabelGap)
            LabelNewerUser(DateTime.MinValue);
        if (user != null) contextIds.Add(user.MessageId);
        var messages = new List<NyaLlmMessage> { mandatory[0], mandatory[1] };
        messages.AddRange(selected);
        messages.AddRange(working);
        var visibleIds = contextIds;
        var segments = new Dictionary<string, List<(int Offset, int Length, bool Eof)>>();
        foreach (var toolMessage in messages.Where(m => m.Role == "tool" || m.Role == "system" && m.Content.StartsWith("{\"prior_tool_call\"", StringComparison.Ordinal)))
            VisitMessageResults(toolMessage.Content, (id, node) =>
            {
                if (node["text"] is not System.Text.Json.Nodes.JsonValue text || !text.TryGetValue<string>(out var content) ||
                    node["offset"] is not System.Text.Json.Nodes.JsonValue offset || !offset.TryGetValue<int>(out var start) ||
                    node["message_eof"] is not System.Text.Json.Nodes.JsonValue eof || !eof.TryGetValue<bool>(out var end)) return;
                if (!segments.TryGetValue(id, out var parts)) segments[id] = parts = [];
                parts.Add((start, content.Length, end));
            });
        foreach (var (id, parts) in segments)
        {
            var covered = 0;
            foreach (var part in parts.OrderBy(p => p.Offset))
            {
                if (part.Offset > covered) break;
                covered = Math.Max(covered, part.Offset + part.Length);
                if (part.Eof) visibleIds.Add(id);
            }
        }
        foreach (var toolMessage in messages.Where(m => m.Role == "tool"))
            toolMessage.Content = VisitMessageResults(toolMessage.Content, (id, node) => node["in_current_context"] = visibleIds.Contains(id));
        return messages;
    }

    private static void AppendHistoricalWork(List<NyaLlmMessage> messages, NyaTurnWork work)
    {
        var pair = new List<NyaLlmMessage>();
        AppendWork(pair, work);
        if (pair.Count == 0) return;
        messages.Add(new NyaLlmMessage { Role = "system", Content = JsonSerializer.Serialize(new
        {
            prior_tool_call = pair[0].ToolCalls![0],
            prior_tool_result = JsonSerializer.Deserialize<JsonElement>(pair[1].Content)
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) });
    }

    private static readonly TimeSpan HistoryTimeLabelGap = TimeSpan.FromHours(3);

    private static DateTime HistoryUtc(DateTime value)
        => DateTime.SpecifyKind(value, value.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : value.Kind).ToUniversalTime();

    private static void AppendWork(List<NyaLlmMessage> messages, NyaTurnWork work)
    {
        if (work.CallJson == "{}") return;
        var call = JsonSerializer.Deserialize<NyaToolCall>(work.CallJson)!;
        if (work.IsHidden) call.FunctionArguments = "{}";
        messages.Add(new NyaLlmMessage { Role = "assistant", Content = "", ToolCalls = [call] });
        messages.Add(new NyaLlmMessage { Role = "tool", ToolCallId = call.Id, Name = call.FunctionName,
            Content = work.IsHidden ? JsonSerializer.Serialize(new { ok = true, outcome = "hidden", data = (object?)null, effects = JsonSerializer.Deserialize<JsonElement>(work.EffectsJson), error = (object?)null }) :
                VisitMessageResults(work.ResultJson, (_, node) => node["in_current_context"] = false) });
    }

    private static string VisitMessageResults(string json, Action<string, System.Text.Json.Nodes.JsonObject> visit)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json);
        void Walk(System.Text.Json.Nodes.JsonNode? node)
        {
            if (node is System.Text.Json.Nodes.JsonObject obj)
            {
                if (obj["message_id"] is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var id) &&
                    new[] { "text", "snippet", "preview", "content" }.Any(key => obj[key] is System.Text.Json.Nodes.JsonValue)) visit(id, obj);
                foreach (var property in obj.ToArray()) Walk(property.Value);
            }
            else if (node is System.Text.Json.Nodes.JsonArray array) foreach (var item in array) Walk(item);
        }
        Walk(root);
        return root?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? json;
    }
}
