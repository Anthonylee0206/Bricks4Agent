using System.Text.Json;
using Broker.NyaChat.Abstractions;
using static Broker.NyaChat.FactToolPlugin;

namespace Broker.NyaChat;

public sealed class TopicToolPlugin : INyaToolPlugin
{
    private readonly NyaMemoryService _memory;
    private readonly NyaAuditLogger _audit;

    public TopicToolPlugin(NyaMemoryService memoryService, NyaAuditLogger auditLogger, ILogger<TopicToolPlugin> logger)
    {
        _memory = memoryService;
        _audit = auditLogger;
    }

    public IReadOnlyList<NyaToolSchema> GetTools() => new[]
    {
        TopicSchema("new_topic", "立即開新聊天室並切換；整則使用者訊息歸最後聊天室，歷史保留原位。", Text("title", max: 128)),
        TopicSchema("switch_topic", "立即切換現有聊天室；可用 ID 或標題，重複標題回候選，不能默選。", Text("topic", true, 128)),
        TopicSchema("rename_topic", "改目前聊天室名稱；指定 topic 可改其他聊天室，重複標題需選 ID。", Text("title", true, 128), Text("topic", max: 128)),
        TopicSchema("topic_list", "完整分頁列出目前會話範圍的聊天室；scope current 只列目前聊天室，title 精確篩選會回所有同名候選。", Choice("scope", "all", "current"), Text("title", max: 128), Number("limit", 1, 100), Text("cursor", max: 128))
    };

    private static NyaToolSchema TopicSchema(string name, string description, params NyaToolParam[] parameters)
        => new() { Name = name, Description = description, CapabilityStatement = description, Group = "topic", DefaultEnabled = true, Parameters = parameters };

    private static object TopicEntry(NyaTopicEntry topic) => new { topic_id = topic.TopicId, title = topic.Title, is_active = topic.IsActive,
        created_at = topic.CreatedAt, message_count = topic.MsgCount };

    public Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (context.Scope == "external") return Task.FromResult(NyaToolResult.Error("external_scope", "scope", "外部明確會話不能操作聊天室。"));
            var validation = NyaToolArgValidator.ValidateError(GetTools().First(s => s.Name == context.ToolName), context.ArgumentsJson);
            if (validation != null) return Task.FromResult(NyaToolResult.Error("invalid_args", validation.Field, validation.Message));
            using var doc = JsonDocument.Parse(context.ArgumentsJson);
            var args = doc.RootElement;
            var turn = _memory.GetTurn(context.UserId, context.TraceId);
            var current = turn?.CurrentTopicId ?? context.ConversationId;
            if (context.ToolName == "topic_list")
            {
                var scope = Str(args, "scope") ?? "all";
                var title = Str(args, "title");
                var topics = _memory.ListTopicsWithCount(context.UserId).Where(t => scope != "current" || t.TopicId == current).Where(t => title == null || t.Title == title).ToList();
                return Task.FromResult(_memory.PageMemory(context, JsonSerializer.Serialize(new { tool = "topic_list", scope, title, current = scope == "current" ? current : null }), Str(args, "cursor"), topics.Select(t => t.TopicId).ToArray(), Int(args, "limit", 20), id =>
                    topics.FirstOrDefault(t => t.TopicId == id) is { } topic ? TopicEntry(topic) : null));
            }
            NyaTopic? target;
            if (context.ToolName == "new_topic")
            {
                target = turn == null ? _memory.CreateNewTopic(context.UserId, Str(args, "title")) : _memory.ChangeTurnTopic(turn, null, Str(args, "title"));
            }
            else
            {
                var key = Str(args, "topic") ?? current;
                var candidates = _memory.FindTopics(context.UserId, key ?? "");
                if (candidates.Count == 0) return Task.FromResult(NyaToolResult.Error("topic_not_found", "topic", "找不到聊天室。"));
                if (candidates.Count > 1) return Task.FromResult(NyaToolResult.Envelope(new { candidates = candidates.Select(TopicEntry) }, "needs_confirmation"));
                var id = candidates[0].TopicId;
                if (context.ToolName == "rename_topic")
                {
                    if (candidates[0].Title == Str(args, "title")) return Task.FromResult(NyaToolResult.Envelope(new { topic_id = id }, "noop"));
                    target = _memory.RenameTopic(context.UserId, id, Str(args, "title")!, context.TraceId);
                }
                else
                {
                    if (id == current) return Task.FromResult(NyaToolResult.Envelope(new { topic_id = id }, "noop"));
                    target = turn == null ? _memory.SwitchTopic(context.UserId, id) : _memory.ChangeTurnTopic(turn, id);
                }
            }
            if (context.ToolName == "new_topic") _audit.LogTopicCreate(context.UserId, target!.TopicId, target.Title);
            else if (context.ToolName == "switch_topic") _audit.LogTopicSwitch(context.UserId, current, target!.TopicId);
            else _audit.LogTopicRename(context.UserId, target!.TopicId, target.Title!);
            return Task.FromResult(NyaToolResult.Envelope(new { topic_id = target!.TopicId, title = target.Title, origin_topic_id = turn?.StartTopicId, rule = "立即生效；整則使用者訊息歸最後所在聊天室。" },
                effects: new object[] { new { action = context.ToolName, topic_id = target.TopicId } }));
        }
        catch (NyaMemoryException ex)
        {
            return Task.FromResult(NyaToolResult.Error(ex.Code, ex.Field, ex.Message));
        }
    }
}
