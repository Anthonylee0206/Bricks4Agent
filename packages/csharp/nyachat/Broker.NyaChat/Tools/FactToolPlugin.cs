using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 內建的長期記憶事實工具插件：<c>upsert_fact</c> / <c>delete_fact</c>。
/// </summary>
/// <remarks>
/// <para>
/// 由 <c>NyaChatOrchestrator</c> 內聯的 <c>ExecuteFactTools</c> 原樣搬遷而來，行為等價：
/// 解析參數 → <see cref="NyaMemoryService"/> 寫入/刪除 → 審計 → 回傳 <c>{"success":...}</c> JSON
/// 作為 <see cref="NyaToolResult.LlmContent"/>，由 Orchestrator 統一閉環餵回 LLM 取得自然語言確認。
/// </para>
/// <para>
/// 預設啟用（<see cref="NyaToolSchema.DefaultEnabled"/> = true）；D1 決議事實工具可由 admin 個別關閉，
/// 故仍納入啟停管理（透過 <see cref="NyaToolEnableStore"/>）。<see cref="NyaToolSchema.RequiredPermission"/>
/// 為 null（所有使用者可用）。
/// </para>
/// 插件相依由自身建構式 DI 取得（gaps 0.2），Nya 核心無須知道。
/// </remarks>
public sealed class FactToolPlugin : INyaToolPlugin
{
    private readonly NyaMemoryService _memoryService;
    private readonly NyaAuditLogger _auditLogger;
    private readonly ILogger<FactToolPlugin> _logger;

    public FactToolPlugin(
        NyaMemoryService memoryService,
        NyaAuditLogger auditLogger,
        ILogger<FactToolPlugin> logger)
    {
        _memoryService = memoryService;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public IReadOnlyList<NyaToolSchema> GetTools() => new[]
    {
        new NyaToolSchema
        {
            Name        = "upsert_fact",
            Description = "記住使用者的資訊（新增或更新長期記憶事實）。用於儲存偏好、身份、指令、待辦事項等。",
            Group       = "memory",
            DefaultEnabled = true,
            CapabilityStatement = "我可以記住並更新你的偏好、身份與指令。",
            Parameters = new[]
            {
                new NyaToolParam { Name = "category", Required = true,
                    Description = "分類：identity（身份）、preference（偏好）、context（任務/情況）、instruction（使用者指令）",
                    Enum = new[] { "identity", "preference", "context", "instruction" } },
                new NyaToolParam { Name = "key", Required = true,
                    Description = "事實的唯一鍵名（英文，底線分隔，例如 user_name、prefers_spicy）" },
                new NyaToolParam { Name = "value", Required = true,
                    Description = "事實的完整內容" },
            }
        },
        new NyaToolSchema
        {
            Name        = "delete_fact",
            Description = "刪除不再有效的記憶事實（例如任務已完成、偏好已改變）。",
            Group       = "memory",
            DefaultEnabled = true,
            CapabilityStatement = "我可以刪除你不再需要的記憶。",
            Parameters = new[]
            {
                new NyaToolParam { Name = "key", Required = true, Description = "要刪除的事實鍵名" },
            }
        }
    };

    public Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken cancellationToken)
    {
        var userId = context.UserId;
        var shortUser = userId[..Math.Min(8, userId.Length)];

        try
        {
            using var doc = JsonDocument.Parse(context.ArgumentsJson);
            var args = doc.RootElement;

            switch (context.ToolName)
            {
                case "upsert_fact":
                {
                    var category = args.TryGetProperty("category", out var c) ? c.GetString() ?? "instruction" : "instruction";
                    var key      = args.TryGetProperty("key",      out var k) ? k.GetString() ?? "" : "";
                    var value    = args.TryGetProperty("value",    out var v) ? v.GetString() ?? "" : "";

                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    {
                        _auditLogger.LogToolResult(userId, context.ToolName, "skipped: empty key or value", "skipped");
                        return Task.FromResult(NyaToolResult.Fail("{\"success\":false,\"error\":\"empty key or value\"}"));
                    }

                    var fact = _memoryService.UpsertFact(userId, category, key, value, null, 1.0f);
                    _auditLogger.LogFactUpsert(userId, key, category, fact.Version);
                    _auditLogger.LogToolResult(userId, context.ToolName, $"upserted {category}/{key} → v{fact.Version}");
                    _logger.LogInformation("[NyaChat/FactTool] upsert_fact key={Key} user={User}", key, shortUser);

                    return Task.FromResult(NyaToolResult.Ok(
                        $"{{\"success\":true,\"category\":\"{category}\",\"key\":\"{key}\",\"version\":{fact.Version}}}"));
                }

                case "delete_fact":
                {
                    var key = args.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        _auditLogger.LogToolResult(userId, context.ToolName, "skipped: empty key", "skipped");
                        return Task.FromResult(NyaToolResult.Fail("{\"success\":false,\"error\":\"empty key\"}"));
                    }

                    _memoryService.DeleteFact(userId, key);
                    _auditLogger.LogMemoryDelete(userId, "fact_key", key);
                    _auditLogger.LogToolResult(userId, context.ToolName, $"deleted fact key={key}");
                    _logger.LogInformation("[NyaChat/FactTool] delete_fact key={Key} user={User}", key, shortUser);

                    return Task.FromResult(NyaToolResult.Ok($"{{\"success\":true,\"key\":\"{key}\"}}"));
                }

                default:
                    return Task.FromResult(NyaToolResult.Fail("{\"success\":false,\"error\":\"unknown tool\"}"));
            }
        }
        catch (Exception ex)
        {
            // Bug #10：例外細節只進 server 端審計/log;回饋 LLM 的一律泛用訊息(不外洩內部錯誤)。
            _auditLogger.LogToolResult(userId, context.ToolName, ex.Message, "error");
            _logger.LogWarning(ex, "[NyaChat/FactTool] Failed to execute {Tool}", context.ToolName);
            return Task.FromResult(NyaToolResult.Fail(NyaToolError.InternalJson()));
        }
    }
}
