using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

public sealed class FactToolPlugin : INyaToolPlugin
{
    private readonly NyaMemoryService _memory;
    private readonly NyaAuditLogger _audit;

    public FactToolPlugin(NyaMemoryService memoryService, NyaAuditLogger auditLogger, ILogger<FactToolPlugin> logger)
    {
        _memory = memoryService;
        _audit = auditLogger;
    }

    internal static NyaToolParam Text(string name, bool required = false, int max = 512, string? pattern = null)
        => new() { Name = name, Required = required, MinLength = 1, MaxLength = max, Pattern = pattern ?? @"\S", Format = name is "date" or "from_date" or "to_date" ? "date" : null };
    internal static NyaToolParam Choice(string name, params string[] values)
        => new() { Name = name, Enum = values };
    internal static NyaToolParam Number(string name, int min, int max)
        => new() { Name = name, Type = "integer", Minimum = min, Maximum = max };
    internal static NyaToolParam Array(string name)
        => new() { Name = name, Type = "array", MinItems = 1, MaxItems = 100, Items = Text("", true, 256) };
    internal static NyaToolSchema Schema(string name, string description, params NyaToolParam[] parameters)
        => new()
        {
            Name = name, Description = description, CapabilityStatement = description, Group = "memory", DefaultEnabled = true, Parameters = parameters,
            AnyOfRequired = name switch
            {
                "note_save" => new[] { new[] { "key" }, new[] { "note_id" } },
                "forget_find" => new[] { new[] { "terms" }, new[] { "note_ids" } },
                "forget" => new[] { new[] { "selection_id" }, new[] { "note_ids" }, new[] { "message_ids" } },
                _ => System.Array.Empty<string[]>()
            },
            ExcludedTogether = name switch
            {
                "note_save" => new[] { new[] { "key", "note_id" } },
                "forget" => new[] { new[] { "selection_id", "note_ids" }, new[] { "selection_id", "message_ids" } },
                _ => System.Array.Empty<string[]>()
            },
            RequiredWhen = System.Array.Empty<NyaToolCondition>()
        };
    internal static string? Str(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) ? value.GetString() : null;
    internal static string[] Strings(JsonElement args, string name)
        => args.TryGetProperty(name, out var value) ? value.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
    internal static int Int(JsonElement args, string name, int fallback)
        => args.TryGetProperty(name, out var value) ? decimal.ToInt32(value.GetDecimal()) : fallback;

    public IReadOnlyList<NyaToolSchema> GetTools() => new[]
    {
        Schema("note_save", "新增用 key，更正用穩定 note_id；key 衝突不覆寫。只記使用者明確告知的資訊。期限只有 expiry 物件：mode keep 沿用、never 長期、on_date 搭配 date 台灣日期含當日。容量滿請先整理。",
            Text("key", max: 128), Text("note_id", max: 128), Text("content", true, 100000),
            new NyaToolParam { Name = "expiry", Type = "object", Properties = new[] {
                new NyaToolParam { Name = "mode", Required = true, Enum = new[] { "keep", "never", "on_date" } },
                Text("date", max: 10, pattern: @"^\d{4}-\d{2}-\d{2}$") }, RequiredWhen = new[] { new NyaToolCondition("mode", "on_date", "date") } }, Array("source_message_ids")),
        Schema("note_list", "完整分頁查詢筆記全文、過期筆記與所有舊版本；scope current 只限目前聊天室，all 為目前會話範圍。",
            Choice("scope", "all", "current"), Text("note_id", max: 128), Number("limit", 1, 100), Text("cursor", max: 128)),
        Schema("forget_find", "先看固定候選與筆記來源訊息，再決定忘記。terms 或 note_ids 至少一組。遺忘只隱藏指定資料；有歧義先問使用者，不能替使用者自填確認。",
            Array("terms"), Array("note_ids"), Choice("scope", "all", "current"), Choice("match", "any", "all")),
        Schema("forget", "在交易內隱藏明確候選、筆記與訊息及其回覆、相關工作紀錄；只回數量和 ID。大範圍先 forget_find。重新告知自然可見，重新保存 key 不解除舊隱藏。",
            Text("selection_id", max: 128), Array("note_ids"), Array("message_ids"))
    };

    public Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var schema = GetTools().First(s => s.Name == context.ToolName);
            var validation = NyaToolArgValidator.ValidateError(schema, context.ArgumentsJson);
            if (validation != null) return Task.FromResult(NyaToolResult.Error("invalid_args", validation.Field, validation.Message));
            using var doc = JsonDocument.Parse(context.ArgumentsJson);
            var args = doc.RootElement;
            NyaToolResult result;
            switch (context.ToolName)
            {
                case "note_save":
                {
                    var noteId = Str(args, "note_id");
                    if (noteId != null && Str(args, "key") != null) throw new NyaMemoryException("exclusive", "key", "更正只用 note_id；新增只用 key。");
                    var sources = Strings(args, "source_message_ids");
                    if (sources.Length == 0 && context.TraceId.Length > 0)
                        sources = _memory.VisibleMessages(context).Where(m => m.TurnId == context.TraceId && m.Role == "user").Select(m => m.MessageId).ToArray();
                    var expiry = args.TryGetProperty("expiry", out var expiryObject) ? Str(expiryObject, "mode")! : "keep";
                    var date = expiryObject.ValueKind == JsonValueKind.Object ? Str(expiryObject, "date") : null;
                    var note = _memory.SaveNote(context, noteId, Str(args, "key"), Str(args, "content")!, expiry, date, sources);
                    result = NyaToolResult.Envelope(new { note_id = note.NoteId, key = note.Key, version = note.Version, expires_at = note.ExpiresAt, source_message_ids = sources },
                        effects: new object[] { new { action = "note_save", note_id = note.NoteId, version = note.Version } });
                    break;
                }
                case "note_list":
                {
                    var scope = Str(args, "scope") ?? "all";
                    var noteId = Str(args, "note_id");
                    var notes = _memory.ListNotes(context.UserId, context.Scope, context.ExternalConversationId, false)
                        .Where(n => noteId == null || n.NoteId == noteId)
                        .Where(n => scope != "current" || n.TopicId == context.ConversationId).ToList();
                    result = _memory.PageMemory(context, JsonSerializer.Serialize(new { tool = "note_list", scope, noteId, topic = scope == "current" ? context.ConversationId : null }), Str(args, "cursor"), notes.Select(n => n.VersionId).ToArray(), Int(args, "limit", 20), id =>
                    {
                        var note = notes.FirstOrDefault(n => n.VersionId == id);
                        return note == null ? null : new { note_id = note.NoteId, version_id = note.VersionId, key = note.Key, content = note.Content,
                            version = note.Version, is_current = note.IsCurrent, expired = note.ExpiresAt <= DateTime.UtcNow, expires_at = note.ExpiresAt,
                            source_message_ids = StringsFromJson(note.SourceMessageIdsJson), topic_id = note.TopicId, created_at = note.CreatedAt };
                    });
                    break;
                }
                case "forget_find":
                    if (Strings(args, "terms").Length + Strings(args, "note_ids").Length == 0) throw new NyaMemoryException("required", "terms", "提供 terms 或 note_ids。");
                    result = _memory.FindForget(context, Strings(args, "terms"), Strings(args, "note_ids"), Str(args, "match") ?? "any", Str(args, "scope") == "current");
                    break;
                case "forget":
                    if ((Str(args, "selection_id") == null ? 0 : 1) + (Strings(args, "note_ids").Length > 0 ? 1 : 0) + (Strings(args, "message_ids").Length > 0 ? 1 : 0) == 0)
                        throw new NyaMemoryException("required", "selection_id", "提供 selection_id、note_ids 或 message_ids。");
                    if (Str(args, "selection_id") != null && Strings(args, "note_ids").Length + Strings(args, "message_ids").Length > 0)
                        throw new NyaMemoryException("exclusive", "selection_id", "selection_id 不可與其他 ID 混用。");
                    result = _memory.Forget(context, Str(args, "selection_id"), Strings(args, "note_ids"), Strings(args, "message_ids"));
                    break;
                default:
                    result = NyaToolResult.Error("unknown_tool", "tool", "未知工具。");
                    break;
            }
            _audit.LogMemoryTool(context.UserId, context.ToolName, context.ArgumentsJson, result.LlmContent, context.TraceId);
            return Task.FromResult(result);
        }
        catch (NyaMemoryException ex)
        {
            return Task.FromResult(NyaToolResult.Error(ex.Code, ex.Field, ex.Message));
        }
        catch (JsonException)
        {
            return Task.FromResult(NyaToolResult.Error("invalid_args", "arguments", "預期 JSON object。"));
        }
        catch (Exception)
        {
            return Task.FromResult(NyaToolResult.Error("internal_error", "tool", "工具執行失敗，請稍後再試。"));
        }
    }

    private static string[] StringsFromJson(string json) => JsonSerializer.Deserialize<string[]>(json) ?? [];
}
