using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 內建話題操作工具插件：<c>new_topic</c> / <c>switch_topic</c> / <c>rename_topic</c>。
/// </summary>
/// <remarks>
/// <para>
/// 讓 LLM 能在對話中開新話題、切換話題、重新命名話題；每次執行都寫審計，並回傳結構化結果給 LLM
/// （由 Orchestrator 統一閉環餵回，產生自然語言確認）。預設擁有、預設開啟
/// （<see cref="NyaToolSchema.DefaultEnabled"/> = true、<see cref="NyaToolSchema.RequiredPermission"/> = null）。
/// </para>
/// <para>
/// 切換時機（與 Orchestrator 設計一致）：<see cref="NyaToolContext.ConversationId"/> 為「本輪」話題；
/// new/switch 只改 DB 的 active 話題，<b>本輪訊息仍寫舊話題、下一輪起生效</b>，故此插件不需處理本輪歸屬。
/// rename 預設改的是 <see cref="NyaToolContext.ConversationId"/>（當前話題），除非指定 topic。
/// </para>
/// </remarks>
public sealed class TopicToolPlugin : INyaToolPlugin
{
    private readonly NyaMemoryService _memoryService;
    private readonly NyaAuditLogger _auditLogger;
    private readonly ILogger<TopicToolPlugin> _logger;

    public TopicToolPlugin(
        NyaMemoryService memoryService,
        NyaAuditLogger auditLogger,
        ILogger<TopicToolPlugin> logger)
    {
        _memoryService = memoryService;
        _auditLogger = auditLogger;
        _logger = logger;
    }

    public IReadOnlyList<NyaToolSchema> GetTools() => new[]
    {
        new NyaToolSchema
        {
            Name        = "new_topic",
            Description = "開一個新話題並切換過去。用於使用者想開始一段全新、與目前無關的對話時。新話題的對話與摘要會與其他話題獨立。",
            Group       = "topic",
            DefaultEnabled = true,
            CapabilityStatement = "我可以幫你開一個新話題，把新的對話跟舊的分開。",
            Parameters = new[]
            {
                new NyaToolParam { Name = "title", Required = false, Description = "新話題的標題（可選，省略則自動命名）" },
            }
        },
        new NyaToolSchema
        {
            Name        = "switch_topic",
            Description = "切換到一個已存在的話題。用於使用者想回到先前的某個話題繼續。",
            Group       = "topic",
            DefaultEnabled = true,
            CapabilityStatement = "我可以幫你切換到先前的話題。",
            Parameters = new[]
            {
                new NyaToolParam { Name = "topic", Required = true, Description = "目標話題的 topic_id 或標題" },
            }
        },
        new NyaToolSchema
        {
            Name        = "rename_topic",
            Description = "重新命名話題。預設改目前話題的標題，除非指定 topic。",
            Group       = "topic",
            DefaultEnabled = true,
            CapabilityStatement = "我可以幫你把話題改名。",
            Parameters = new[]
            {
                new NyaToolParam { Name = "title", Required = true, Description = "新的話題標題" },
                new NyaToolParam { Name = "topic", Required = false, Description = "要改名的話題 topic_id 或舊標題（可選，省略則為目前話題）" },
            }
        }
    };

    public Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken cancellationToken)
    {
        var userId = context.UserId;
        var shortUser = userId[..Math.Min(8, userId.Length)];
        var currentTopicId = context.ConversationId;

        try
        {
            using var doc = JsonDocument.Parse(context.ArgumentsJson);
            var args = doc.RootElement;
            string? Arg(string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

            switch (context.ToolName)
            {
                case "new_topic":
                {
                    var title = Arg("title");
                    var topic = _memoryService.CreateNewTopic(userId, title);
                    _auditLogger.LogTopicCreate(userId, topic.TopicId, topic.Title);
                    _logger.LogInformation("[NyaChat/TopicTool] new_topic id={Id} user={User}", topic.TopicId, shortUser);

                    return Ok(new
                    {
                        success = true,
                        action = "new_topic",
                        topic_id = topic.TopicId,
                        title = topic.Title,
                        note = "新話題已建立並切換；本輪對話仍屬舊話題，下一則訊息起進入新話題。"
                    });
                }

                case "switch_topic":
                {
                    var key = Arg("topic");
                    if (string.IsNullOrWhiteSpace(key))
                        return Fail("missing_topic", "請提供要切換的話題 topic_id 或標題。");

                    var target = _memoryService.ResolveTopic(userId, key);
                    if (target == null)
                        return Fail("topic_not_found", $"找不到話題「{key}」，請確認名稱或 topic_id。");

                    _memoryService.SwitchTopic(userId, target.TopicId);
                    _auditLogger.LogTopicSwitch(userId, currentTopicId, target.TopicId);
                    _logger.LogInformation("[NyaChat/TopicTool] switch_topic → {Id} user={User}", target.TopicId, shortUser);

                    return Ok(new
                    {
                        success = true,
                        action = "switch_topic",
                        topic_id = target.TopicId,
                        title = target.Title,
                        note = "已切換話題；本輪對話仍屬原話題，下一則訊息起進入目標話題。"
                    });
                }

                case "rename_topic":
                {
                    var title = Arg("title");
                    if (string.IsNullOrWhiteSpace(title))
                        return Fail("missing_title", "請提供新的話題標題。");

                    var key = Arg("topic");
                    var targetTopicId = !string.IsNullOrWhiteSpace(key)
                        ? _memoryService.ResolveTopic(userId, key!)?.TopicId
                        : currentTopicId;

                    if (string.IsNullOrWhiteSpace(targetTopicId))
                        return Fail("topic_not_found", "找不到要改名的話題。");

                    var renamed = _memoryService.RenameTopic(userId, targetTopicId!, title!);
                    if (renamed == null)
                        return Fail("topic_not_found", "找不到要改名的話題。");

                    _auditLogger.LogTopicRename(userId, renamed.TopicId, title!);
                    _logger.LogInformation("[NyaChat/TopicTool] rename_topic {Id} user={User}", renamed.TopicId, shortUser);

                    return Ok(new
                    {
                        success = true,
                        action = "rename_topic",
                        topic_id = renamed.TopicId,
                        title = renamed.Title
                    });
                }

                default:
                    return Fail("unknown_tool", "未知的話題工具。");
            }
        }
        catch (Exception ex)
        {
            // Bug #10：例外細節只進 server 端 log/審計；回饋 LLM 的一律泛用訊息。
            _auditLogger.LogToolResult(userId, context.ToolName, ex.Message, "error");
            _logger.LogWarning(ex, "[NyaChat/TopicTool] Failed to execute {Tool}", context.ToolName);
            return Task.FromResult(NyaToolResult.Fail(NyaToolError.InternalJson()));
        }
    }

    private static Task<NyaToolResult> Ok(object payload)
        => Task.FromResult(NyaToolResult.Ok(JsonSerializer.Serialize(payload)));

    private static Task<NyaToolResult> Fail(string error, string message)
        => Task.FromResult(NyaToolResult.Fail(
            JsonSerializer.Serialize(new { success = false, error, message })));
}
