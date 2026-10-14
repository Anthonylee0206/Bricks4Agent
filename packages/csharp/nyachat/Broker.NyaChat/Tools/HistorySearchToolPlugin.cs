using System.Text.Json;
using Broker.NyaChat.Abstractions;
using static Broker.NyaChat.FactToolPlugin;

namespace Broker.NyaChat;

public sealed class HistorySearchToolPlugin : INyaToolPlugin
{
    private readonly NyaMemoryService _memory;

    public HistorySearchToolPlugin(NyaMemoryService memoryService, NyaAuditLogger auditLogger, ILogger<HistorySearchToolPlugin> logger)
    {
        _memory = memoryService;
    }

    public IReadOnlyList<NyaToolSchema> GetTools() => new[]
    {
        Schema("search_history", "搜尋可見對話，回傳 ID、聊天室、日期、說話者、命中片段與目前上下文狀態。支援聊天室與台灣日期篩選、any/all、穩定 cursor；cursor 過期需重新查詢。",
            Text("query", true, 512), Choice("match", "any", "all"), Text("topic", max: 128), Text("from_date", max: 10, pattern: @"^\d{4}-\d{2}-\d{2}$"), Text("to_date", max: 10, pattern: @"^\d{4}-\d{2}-\d{2}$"), Number("limit", 1, 100), Text("cursor", max: 128)),
        Schema("read_history", "讀訊息及前後實際對話；長訊息依 cursor 分段直到 eof。message_eof 表示單則結尾，eof 表示全部讀完。只讀目前會話範圍可見資料。",
            Text("message_id", true, 128), Number("before", 0, 20), Number("after", 0, 20), Number("segment_length", 1, 4000), Text("cursor", max: 128))
    };

    public Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            var validation = NyaToolArgValidator.ValidateError(GetTools().First(s => s.Name == context.ToolName), context.ArgumentsJson);
            if (validation != null) return Task.FromResult(NyaToolResult.Error("invalid_args", validation.Field, validation.Message));
            using var doc = JsonDocument.Parse(context.ArgumentsJson);
            var args = doc.RootElement;
            return Task.FromResult(context.ToolName == "search_history"
                ? _memory.SearchHistory(context, Str(args, "query")!, Str(args, "match") ?? "any", Str(args, "topic"), Str(args, "from_date"), Str(args, "to_date"), Int(args, "limit", 10), Str(args, "cursor"))
                : _memory.ReadHistory(context, Str(args, "message_id")!, Int(args, "before", 1), Int(args, "after", 1), Int(args, "segment_length", 2000), Str(args, "cursor")));
        }
        catch (NyaMemoryException ex)
        {
            return Task.FromResult(NyaToolResult.Error(ex.Code, ex.Field, ex.Message));
        }
    }
}
