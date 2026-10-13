using System.Text.Encodings.Web;
using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 內建舊對話搜尋工具 <c>search_history</c>：讓模型在需要時自行翻閱原始對話紀錄。
/// 回傳命中的使用者訊息與緊接的下一則回應（附日期）；已遺忘的事項不會被帶回。
/// </summary>
public sealed class HistorySearchToolPlugin : INyaToolPlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly NyaMemoryService _memoryService;
    private readonly NyaAuditLogger _auditLogger;
    private readonly ILogger<HistorySearchToolPlugin> _logger;

    public HistorySearchToolPlugin(
        NyaMemoryService memoryService,
        NyaAuditLogger auditLogger,
        ILogger<HistorySearchToolPlugin> logger)
    {
        _memoryService = memoryService;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public IReadOnlyList<NyaToolSchema> GetTools() => new[]
    {
        new NyaToolSchema
        {
            Name        = "search_history",
            Description = "搜尋你和這位使用者過去的對話原文。使用者提到以前聊過的事、問你上次說了什麼、或你需要回想更早的內容而上面的近期對話裡沒有時使用。可以換不同關鍵字多查幾次；查不到就照實說查不到。",
            Group       = "memory",
            DefaultEnabled = true,
            CapabilityStatement = "我可以翻我們以前的對話紀錄，找回聊過的內容。",
            Parameters = new[]
            {
                new NyaToolParam { Name = "query", Required = true,
                    Description = "關鍵字，多個以空白分隔（例如：台鐵 高雄）" },
                new NyaToolParam { Name = "limit", Required = false, Type = "integer",
                    Description = "最多回傳幾筆，預設 5，上限 20" },
            }
        }
    };

    public Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken cancellationToken)
    {
        var userId = context.UserId;
        try
        {
            using var doc = JsonDocument.Parse(context.ArgumentsJson);
            var args = doc.RootElement;
            var query = args.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String ? q.GetString() ?? "" : "";
            var limit = 5;
            if (args.TryGetProperty("limit", out var l))
            {
                if (l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var n)) limit = n;
                else if (l.ValueKind == JsonValueKind.String && int.TryParse(l.GetString(), out var m)) limit = m;
            }

            if (string.IsNullOrWhiteSpace(query))
                return Task.FromResult(NyaToolResult.Fail(Serialize(new { success = false, error = "missing_query", message = "請提供要搜尋的關鍵字。" })));

            var scope = _memoryService.IsUserTopic(userId, context.ConversationId) ? null : context.ConversationId;
            var hits = _memoryService.SearchConversation(userId, query, limit, scope);
            var results = hits.Select(x =>
            {
                var h = x.Hit;
                var next = x.Next;
                return new
                {
                    date = LocalTime(h.CreatedAt),
                    speaker = Speaker(h.Role),
                    text = Clip(h.Content),
                    followed_by = next == null ? null : new { speaker = Speaker(next.Role), text = Clip(next.Content) }
                };
            }).ToList();

            _auditLogger.LogToolResult(userId, context.ToolName, $"query={query} hits={results.Count}");
            _logger.LogInformation("[NyaChat/HistoryTool] search_history hits={Hits} user={User}",
                results.Count, userId[..Math.Min(8, userId.Length)]);

            return Task.FromResult(NyaToolResult.Ok(Serialize(new
            {
                success = true,
                query,
                found = results.Count,
                results,
                note = results.Count == 0 ? "沒有找到相關的過去對話。" : null
            })));
        }
        catch (Exception ex)
        {
            _auditLogger.LogToolResult(userId, context.ToolName, ex.Message, "error");
            _logger.LogWarning(ex, "[NyaChat/HistoryTool] Failed to execute {Tool}", context.ToolName);
            return Task.FromResult(NyaToolResult.Fail(NyaToolError.InternalJson()));
        }
    }

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, JsonOptions);

    private static string Speaker(string role) => role == "assistant" ? "你" : role == "user" ? "使用者" : role;

    private static string Clip(string text) => text.Length <= 300 ? text : text[..300] + "…";

    private static string LocalTime(DateTime utc)
        => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
