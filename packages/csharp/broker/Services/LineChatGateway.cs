using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;

namespace Broker.Services;

/// <summary>
/// High-level conversation gateway for LINE.
///
/// This service owns:
/// - conversation history persistence
/// - optional RAG augmentation
/// - calls to the high-level LLM binding
///
/// It intentionally does not use the execution/runtime LLM binding.
/// </summary>
public class LineChatGateway
{
    private readonly LineChatGatewayOptions _options;
    private readonly HighLevelLlmOptions _highLevelLlmOptions;
    private readonly HttpClient _httpClient;
    private readonly BrokerDb _db;
    private readonly EmbeddingService _embeddingService;
    private readonly RagPipelineService _ragPipeline;
    private readonly ILogger<LineChatGateway> _logger;

    public LineChatGateway(
        LineChatGatewayOptions options,
        HighLevelLlmOptions highLevelLlmOptions,
        IHttpClientFactory httpClientFactory,
        BrokerDb db,
        EmbeddingService embeddingService,
        RagPipelineService ragPipeline,
        ILogger<LineChatGateway> logger)
    {
        _options = options;
        _highLevelLlmOptions = highLevelLlmOptions;
        _httpClient = httpClientFactory.CreateClient("high-level-llm");
        _db = db;
        _embeddingService = embeddingService;
        _ragPipeline = ragPipeline;
        _logger = logger;
    }

    public async Task<ChatResult> ChatAsync(string userId, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(message))
        {
            return new ChatResult
            {
                Reply = "使用者識別與訊息內容不可為空。",
                Error = "empty_input"
            };
        }

        try
        {
            var history = LoadConversationHistory(userId);
            _logger.LogInformation(
                "[LineChatGW] User={User} history={Count} msg={Msg}",
                userId[..Math.Min(8, userId.Length)],
                history.Count,
                Truncate(message, 50));

            string? ragContext = null;
            List<RagSnippet>? ragSnippets = null;
            if (_options.RagEnabled && ShouldUseRag(message))
            {
                (ragContext, ragSnippets) = await RetrieveRagContext(message, ct);
            }

            var messages = BuildMessages(history, message, ragContext);
            var llmReply = await CallLlmAsync(messages, ct);
            if (llmReply == null)
            {
                return new ChatResult
                {
                    Reply = "抱歉，AI 服務暫時無法回應，請稍後再試。",
                    Error = "llm_unavailable"
                };
            }

            SaveMessage(userId, "user", message);
            SaveMessage(userId, "assistant", llmReply);

            _logger.LogInformation(
                "[LineChatGW] Reply to {User}: {Reply}",
                userId[..Math.Min(8, userId.Length)],
                Truncate(llmReply, 80));

            return new ChatResult
            {
                Reply = llmReply,
                RagSnippets = ragSnippets,
                HistoryCount = history.Count + 2
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LineChatGW] Error for user {User}", userId);
            return new ChatResult
            {
                Reply = "處理 LINE 對話時發生錯誤，請稍後再試。",
                Error = ex.Message
            };
        }
    }

    public async Task<string?> SummarizeQueryResultsAsync(
        string userId,
        string queryKind,
        string originalQuery,
        IReadOnlyList<HighLevelQuerySearchResult> results,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(originalQuery) ||
            results.Count == 0)
        {
            return null;
        }

        try
        {
            var cappedResults = results
                .Where(result => !string.IsNullOrWhiteSpace(result.Title) || !string.IsNullOrWhiteSpace(result.Snippet))
                .OrderBy(result => result.Rank)
                .Take(5)
                .ToArray();

            if (cappedResults.Length == 0)
                return null;

            var prompt = BuildQueryResultSummaryPrompt(queryKind, originalQuery, cappedResults);
            var messages = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] =
                        "你是高階查詢協調者。你的任務是根據工具取得的多筆結果，整理出直接可用的答案。" +
                        "請優先回答使用者真正想知道的內容，而不是單純重列搜尋結果。" +
                        "若結果不足以得出確定答案，請明確說出不確定之處，並指出哪一筆來源最值得點開。" +
                        "回覆請使用繁體中文。"
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = prompt
                }
            };

            return await CallLlmAsync(messages, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LineChatGW] Query-result synthesis failed for {User}", userId);
            return null;
        }
    }

    public List<ConversationSummary> ListConversations()
    {
        // 只採信系統元件在 global 範圍寫入的對話紀錄
        var entries = _db.Query<SharedContextEntry>(
            $@"SELECT * FROM shared_context_entries
              WHERE key LIKE 'convlog:%' AND {SystemContextDocuments.TrustedGlobalCondition()}
              ORDER BY created_at DESC");

        var result = new List<ConversationSummary>();
        foreach (var entry in entries)
        {
            var uid = entry.Key.Length > 8 ? entry.Key[8..] : entry.Key;
            var messages = ParseMessages(entry.ContentRef);
            var lastMsg = messages.LastOrDefault();
            result.Add(new ConversationSummary
            {
                UserId = uid,
                MessageCount = messages.Count,
                LastMessage = lastMsg?.Content ?? "",
                LastRole = lastMsg?.Role ?? "",
                LastTimestamp = lastMsg?.Timestamp ?? entry.CreatedAt.ToString("O")
            });
        }

        return result;
    }

    public List<ConvMessage> GetConversation(string userId)
        => LoadConversationHistory(userId);

    public void ClearConversation(string userId)
    {
        var key = $"convlog:{userId}";
        _db.Execute(
            "DELETE FROM shared_context_entries WHERE key = @key AND task_id = 'global'",
            new { key });
        _logger.LogInformation("[LineChatGW] Cleared conversation for {User}", userId);
    }

    private List<ConvMessage> LoadConversationHistory(string userId)
    {
        var key = $"convlog:{userId}";
        var entry = _db.Query<SharedContextEntry>(
            $@"SELECT content_ref FROM shared_context_entries
              WHERE key = @key AND {SystemContextDocuments.TrustedGlobalCondition()}
              ORDER BY version DESC LIMIT 1",
            new { key }).FirstOrDefault();

        if (entry == null || string.IsNullOrEmpty(entry.ContentRef))
            return new List<ConvMessage>();

        var messages = ParseMessages(entry.ContentRef);
        if (messages.Count > _options.MaxConversationHistory)
            messages = messages.Skip(messages.Count - _options.MaxConversationHistory).ToList();

        return messages;
    }

    private void SaveMessage(string userId, string role, string content)
    {
        var key = $"convlog:{userId}";
        var existing = LoadConversationHistory(userId);
        existing.Add(new ConvMessage
        {
            Role = role,
            Content = content,
            Timestamp = DateTime.UtcNow.ToString("O")
        });

        if (existing.Count > _options.MaxConversationHistory * 2)
            existing = existing.Skip(existing.Count - _options.MaxConversationHistory).ToList();

        var json = JsonSerializer.Serialize(existing);
        var existingEntry = _db.Query<SharedContextEntry>(
            $"SELECT entry_id FROM shared_context_entries WHERE key = @key AND {SystemContextDocuments.TrustedGlobalCondition()} LIMIT 1",
            new { key }).FirstOrDefault();

        if (existingEntry != null)
        {
            _db.Execute(
                $@"UPDATE shared_context_entries
                  SET content_ref = @json, version = version + 1
                  WHERE key = @key AND {SystemContextDocuments.TrustedGlobalCondition()}",
                new { json, key });
        }
        else
        {
            var entry = new SharedContextEntry
            {
                EntryId = $"ctx_{Guid.NewGuid():N}"[..24],
                TaskId = "global",
                DocumentId = $"convlog:{userId}",
                Key = key,
                ContentRef = json,
                ContentType = "application/json",
                AuthorPrincipalId = "system:line-gateway",
                Acl = """{"read":["*"],"write":["system:line-gateway"]}""",
                Version = 1,
                CreatedAt = DateTime.UtcNow
            };
            _db.Insert(entry);
        }
    }

    private async Task<(string? context, List<RagSnippet>? snippets)> RetrieveRagContext(
        string query,
        CancellationToken ct)
    {
        try
        {
            var topK = _options.RagTopK;
            var retrievalQuery = BuildRagRetrievalQuery(query);
            var retrieval = new RagRetrievalService(
                _db,
                _embeddingService,
                _ragPipeline,
                message => _logger.LogWarning("{Message}", message));
            var response = await retrieval.RetrieveAsync(new RagRetrieveRequest
            {
                Query = retrievalQuery,
                Mode = "hybrid",
                Limit = topK,
                Threshold = 0.2f,
                Rewrite = false,
                Rerank = false
            }, "global", ct);

            if (response.Results.Count == 0)
                return (null, null);

            var coreSnippets = new List<RagSnippet>();
            var coreBuilder = new StringBuilder();
            coreBuilder.AppendLine("RAG context snippets:");
            coreBuilder.AppendLine();

            foreach (var item in response.Results.Take(topK))
            {
                var display = item.Content.Length > 500 ? item.Content[..500] + "..." : item.Content;
                coreBuilder.AppendLine($"[{item.Key}]");
                coreBuilder.AppendLine(display);
                coreBuilder.AppendLine();

                coreSnippets.Add(new RagSnippet
                {
                    Key = item.Key,
                    Content = display,
                    Score = item.Score
                });
            }

            return (coreBuilder.ToString(), coreSnippets);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[LineChatGW] RAG retrieval failed, continuing without context");
            return (null, null);
        }
    }

    private JsonArray BuildMessages(List<ConvMessage> history, string userMessage, string? ragContext)
    {
        var messages = new JsonArray();
        var systemContent = _options.SystemPrompt;
        if (!string.IsNullOrEmpty(ragContext))
        {
            systemContent += "\n\n" + ragContext;
        }

        messages.Add(new JsonObject
        {
            ["role"] = "system",
            ["content"] = systemContent
        });

        foreach (var msg in history)
        {
            messages.Add(new JsonObject
            {
                ["role"] = msg.Role,
                ["content"] = msg.Content
            });
        }

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = userMessage
        });

        return messages;
    }

    private static string BuildQueryResultSummaryPrompt(
        string queryKind,
        string originalQuery,
        IReadOnlyList<HighLevelQuerySearchResult> results)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"查詢類型：{queryKind}");
        sb.AppendLine($"原始查詢：{originalQuery}");
        sb.AppendLine();
        sb.AppendLine("以下是工具取得的候選結果，請綜合前 3 到 5 筆內容，整理成直接回答：");
        sb.AppendLine();

        foreach (var result in results)
        {
            sb.AppendLine($"[{result.Rank}] {result.Title}");
            if (!string.IsNullOrWhiteSpace(result.Url))
                sb.AppendLine($"URL: {result.Url}");
            if (!string.IsNullOrWhiteSpace(result.Snippet))
                sb.AppendLine($"內容摘要: {result.Snippet}");
            sb.AppendLine();
        }

        sb.AppendLine("輸出要求：");
        sb.AppendLine("1. 先直接回答重點。");
        sb.AppendLine("2. 若有多個可能答案，整理成條列。");
        sb.AppendLine("3. 最後附上“參考來源：”並列出 1 到 3 個最重要的網址。");
        sb.AppendLine("4. 不要逐筆重貼所有搜尋結果。");
        return sb.ToString();
    }

    private async Task<string?> CallLlmAsync(JsonArray messages, CancellationToken ct)
    {
        try
        {
            var provider = (_highLevelLlmOptions.Provider ?? "ollama").Trim().ToLowerInvariant();
            return provider switch
            {
                "ollama" => await SendOllamaChatAsync(messages, ct),
                "anthropic" or "claude" => await HighLevelAnthropicClient.SendAsync(_httpClient, _highLevelLlmOptions, messages, ct),
                _ => string.Equals(_highLevelLlmOptions.ApiFormat, "responses", StringComparison.OrdinalIgnoreCase)
                    ? await SendResponsesApiAsync(messages, ct)
                    : await SendChatCompletionsAsync(messages, ct)
            };
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("[LineChatGW] LLM call timed out");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LineChatGW] LLM call failed");
            return null;
        }
    }

    private async Task<string?> SendOllamaChatAsync(JsonArray messages, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["model"] = _highLevelLlmOptions.DefaultModel,
            ["messages"] = messages,
            ["stream"] = false,
            ["think"] = false,
            ["options"] = new JsonObject
            {
                ["num_predict"] = Math.Max(1, _highLevelLlmOptions.MaxOutputTokens)
            }
        };

        if (_highLevelLlmOptions.DefaultModel == "qwen3.6:35b-a3b-Q4")
        {
            request["options"] = new JsonObject
            {
                ["min_p"] = 0,
                ["presence_penalty"] = 1.5,
                ["repeat_penalty"] = 1,
                ["temperature"] = 1,
                ["top_k"] = 20,
                ["top_p"] = 0.95
            };
        }

        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("api/chat", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("[LineChatGW] High-level LLM returned {Status}: {Error}",
                response.StatusCode, Truncate(error, 200));
            return null;
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);
        var replyContent = doc.RootElement.GetProperty("message").GetProperty("content").GetString();
        return StripThinkTags(replyContent);
    }

    private async Task<string?> SendChatCompletionsAsync(JsonArray messages, CancellationToken ct)
    {
        var request = new JsonObject
        {
            ["model"] = _highLevelLlmOptions.DefaultModel,
            ["messages"] = messages,
            ["stream"] = false
        };

        if (_highLevelLlmOptions.DefaultModel == "qwen3.6:35b-a3b-Q4")
        {
            request["min_p"] = 0;
            request["presence_penalty"] = 1.5;
            request["frequency_penalty"] = 1.0;
            request["repeat_penalty"] = 1;
            request["temperature"] = 1;
            request["top_k"] = 20;
            request["top_p"] = 0.95;
        }

        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("v1/chat/completions", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("[LineChatGW] High-level LLM returned {Status}: {Error}",
                response.StatusCode, Truncate(error, 200));
            return null;
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0)
            return null;

        var replyContent = choices[0].GetProperty("message").GetProperty("content").GetString();
        return StripThinkTags(replyContent);
    }

    private async Task<string?> SendResponsesApiAsync(JsonArray messages, CancellationToken ct)
    {
        var input = new JsonArray();
        foreach (var msg in messages)
        {
            if (msg is not JsonObject obj)
                continue;

            var role = obj["role"]?.GetValue<string>() ?? "user";
            var text = obj["content"]?.GetValue<string>() ?? string.Empty;
            var contentType = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "output_text"
                : "input_text";
            input.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = contentType,
                        ["text"] = text
                    }
                }
            });
        }

        var request = new JsonObject
        {
            ["model"] = _highLevelLlmOptions.DefaultModel,
            ["input"] = input,
            ["stream"] = false
        };

        using var content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("v1/responses", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct);
            _logger.LogError("[LineChatGW] High-level LLM returned {Status}: {Error}",
                response.StatusCode, Truncate(error, 200));
            return null;
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(responseJson);

        if (doc.RootElement.TryGetProperty("output_text", out var outputText) &&
            outputText.ValueKind == JsonValueKind.String)
        {
            return StripThinkTags(outputText.GetString());
        }

        if (doc.RootElement.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var contentItem in contentArray.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("text", out var textNode) &&
                        textNode.ValueKind == JsonValueKind.String)
                    {
                        return StripThinkTags(textNode.GetString());
                    }
                }
            }
        }

        return null;
    }

    private static List<ConvMessage> ParseMessages(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new List<ConvMessage>();

        try
        {
            return JsonSerializer.Deserialize<List<ConvMessage>>(json) ?? new List<ConvMessage>();
        }
        catch
        {
            return new List<ConvMessage>();
        }
    }

    private static string? StripThinkTags(string? replyContent)
    {
        if (string.IsNullOrWhiteSpace(replyContent))
            return replyContent?.Trim();

        if (replyContent.Contains("<think>", StringComparison.Ordinal))
        {
            var thinkEnd = replyContent.IndexOf("</think>", StringComparison.Ordinal);
            if (thinkEnd >= 0)
            {
                replyContent = replyContent[(thinkEnd + 8)..].TrimStart();
            }
        }

        return replyContent.Trim();
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    private bool ShouldUseRag(string message)
    {
        if (_options.RagTriggerKeywords == null || _options.RagTriggerKeywords.Length == 0)
            return true;

        return _options.RagTriggerKeywords.Any(keyword =>
            !string.IsNullOrWhiteSpace(keyword) &&
            message.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildRagRetrievalQuery(string message)
    {
        var terms = new List<string>();
        AddIfContains(terms, message, "消費者保護法");
        AddIfContains(terms, message, "通訊交易");
        AddIfContains(terms, message, "訪問交易");
        AddIfContains(terms, message, "解除契約");
        AddIfContains(terms, message, "退回商品");
        AddIfContains(terms, message, "書面通知");
        AddIfContains(terms, message, "七日");
        AddIfContains(terms, message, "7日");
        AddIfContains(terms, message, "第19條", "第十九條");
        AddIfContains(terms, message, "第18條", "第十八條");
        AddIfContains(terms, message, "合理例外");
        AddIfContains(terms, message, "退費");
        AddIfContains(terms, message, "退貨");
        AddIfContains(terms, message, "契約");
        AddIfContains(terms, message, "consumer protection");
        AddIfContains(terms, message, "contract");
        AddIfContains(terms, message, "legal");

        var compact = terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        return compact.Length > 0
            ? string.Join(' ', compact)
            : message;
    }

    private static void AddIfContains(List<string> terms, string message, string term, string? match = null)
    {
        if (message.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            (match != null && message.Contains(match, StringComparison.OrdinalIgnoreCase)))
        {
            terms.Add(term);
        }
    }
}

public class LineChatGatewayOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxConversationHistory { get; set; } = 20;
    public bool RagEnabled { get; set; } = true;
    public int RagTopK { get; set; } = 3;
    public string[] RagTriggerKeywords { get; set; } =
    {
        "法律",
        "法規",
        "條文",
        "契約",
        "合約",
        "權益",
        "申訴",
        "罰則",
        "民法",
        "刑法",
        "勞基法",
        "消費者",
        "消保",
        "法務",
        "regulation",
        "compliance",
        "consumer protection",
        "contract",
        "law",
        "legal"
    };
    // public string SystemPrompt { get; set; } =
    //     "你是一個透過 LINE 與使用者互動的高階智慧助理。請使用繁體中文，語氣簡潔清楚。"
    //     + " 你的角色是對話、澄清需求、回答一般問題，並在適當時引導使用者使用明確前綴。"
    //     + " 若問題需要受控網路搜尋，請建議使用 ?search 關鍵字。"
    //     + " 若使用者要建立或修改交付物，請引導使用 / 開頭的任務指令。";

    public string SystemPrompt { get; set; } =
        " 你是一個透過 LINE 與使用者互動的高階智慧助理NYA。請使用繁體中文，語氣清楚。"
        + " 若問題需要即時網路搜尋，請建議使用者輸入 `?search 關鍵字`。"
        + " 若使用者要建立、修改程式碼專案或執行複雜任務，請引導使用 `/任務名稱` 開頭的指令。";
}

public class ChatResult
{
    public string Reply { get; set; } = "";
    public string? Error { get; set; }
    public List<RagSnippet>? RagSnippets { get; set; }
    public int HistoryCount { get; set; }
}

public class ConvMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string Timestamp { get; set; } = "";
}

public class ConversationSummary
{
    public string UserId { get; set; } = "";
    public int MessageCount { get; set; }
    public string LastMessage { get; set; } = "";
    public string LastRole { get; set; } = "";
    public string LastTimestamp { get; set; } = "";
}

public class RagSnippet
{
    public string Key { get; set; } = "";
    public string Content { get; set; } = "";
    public float Score { get; set; }
}

public class RagFtsResult
{
    public string? SourceKey { get; set; }
    public string? Content { get; set; }
    public double Rank { get; set; }
}
