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
            Description = "把使用者明確說出、之後仍會用到的事記成筆記（新增或更新）：稱呼與身分、長期偏好、明確要你記住的事、有期限的近況。一次性的查詢、問題、玩笑、情緒不要推論成偏好。同一件事改變時用同一個 key 更新。",
            Group       = "memory",
            DefaultEnabled = true,
            CapabilityStatement = "我可以把你明確告訴我的事記下來，之後也能更正。",
            Parameters = new[]
            {
                new NyaToolParam { Name = "category", Required = true,
                    Description = "分類：identity（身份）、preference（偏好）、context（任務/情況）、instruction（使用者指令）",
                    Enum = new[] { "identity", "preference", "context", "instruction" } },
                new NyaToolParam { Name = "key", Required = true,
                    Description = "事實的唯一鍵名（英文，底線分隔，例如 user_name、prefers_spicy）" },
                new NyaToolParam { Name = "value", Required = true,
                    Description = "筆記內容（簡短、具體，不加推論）" },
                new NyaToolParam { Name = "source_text", Required = false,
                    Description = "使用者的原話（記下這則筆記的依據）" },
                new NyaToolParam { Name = "expires_in_days", Required = false, Type = "integer",
                    Description = "幾天後失效（整數）；只用於有期限的近況，例如明天的面試。長期資訊不要填；更新時不填表示沿用原本期限，填 0 表示改為長期有效" },
            }
        },
        new NyaToolSchema
        {
            Name        = "delete_fact",
            Description = "遺忘一則筆記：使用者要你忘記、說記錯了、或資訊已不再成立時使用。遺忘後不會再被提起，除非使用者重新告訴你。",
            Group       = "memory",
            DefaultEnabled = true,
            CapabilityStatement = "你要我忘掉的事，我會真的忘掉。",
            Parameters = new[]
            {
                new NyaToolParam { Name = "key", Required = true, Description = "要遺忘的筆記 key" },
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
                    var existing = _memoryService.GetFactHistory(userId, key).FirstOrDefault(x => x.IsActive);
                    var source   = args.TryGetProperty("source_text", out var st) && st.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(st.GetString())
                        ? st.GetString()
                        : existing?.SourceText;
                    var expiresAt = existing?.ExpiresAt;
                    if (args.TryGetProperty("expires_in_days", out var ed) && ed.ValueKind != JsonValueKind.Null)
                    {
                        int? days = ed.ValueKind == JsonValueKind.Number && ed.TryGetInt32(out var n) ? n
                                  : ed.ValueKind == JsonValueKind.String && int.TryParse(ed.GetString(), out var m) ? m : null;
                        if (days is null || days < 0)
                        {
                            _auditLogger.LogToolResult(userId, context.ToolName, "invalid expires_in_days", "skipped");
                            return Task.FromResult(NyaToolResult.Fail(
                                "{\"success\":false,\"error\":\"invalid_expires_in_days\",\"message\":\"expires_in_days 必須是 0 以上的整數；0 表示改為長期有效\"}"));
                        }
                        expiresAt = days == 0 ? null : DateTime.UtcNow.AddDays(Math.Min(days.Value, 3650));
                    }

                    if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                    {
                        _auditLogger.LogToolResult(userId, context.ToolName, "skipped: empty key or value", "skipped");
                        return Task.FromResult(NyaToolResult.Fail("{\"success\":false,\"error\":\"empty key or value\"}"));
                    }

                    var fact = _memoryService.UpsertFact(userId, category, key, value, null, 1.0f, source, expiresAt);
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

                    if (!_memoryService.ForgetFact(userId, key))
                    {
                        _auditLogger.LogToolResult(userId, context.ToolName, $"no note key={key}", "skipped");
                        return Task.FromResult(NyaToolResult.Fail($"{{\"success\":false,\"error\":\"note_not_found\",\"key\":\"{key}\"}}"));
                    }
                    _auditLogger.LogMemoryDelete(userId, "fact_key", key);
                    _auditLogger.LogToolResult(userId, context.ToolName, $"forgot fact key={key}");
                    _logger.LogInformation("[NyaChat/FactTool] delete_fact key={Key} user={User}", key, shortUser);

                    return Task.FromResult(NyaToolResult.Ok($"{{\"success\":true,\"key\":\"{key}\",\"forgotten\":true}}"));
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
