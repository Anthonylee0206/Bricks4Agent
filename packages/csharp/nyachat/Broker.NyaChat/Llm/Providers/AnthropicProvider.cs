using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.NyaChat;

/// <summary>Anthropic Messages API provider（<c>POST v1/messages</c>，含 tool-use，Doc 3 Plan B）。</summary>
public sealed class AnthropicProvider : INyaLlmProvider
{
    private const string AnthropicVersion = "2023-06-01";
    private const int DefaultMaxTokens = 4096;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AnthropicProvider> _logger;

    public string Key => "anthropic";
    public bool SupportsTools => true;

    public AnthropicProvider(IHttpClientFactory httpClientFactory, ILogger<AnthropicProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<NyaLlmResponse?> SendAsync(NyaLlmProviderRequest pr, CancellationToken ct)
    {
        var profile = pr.Profile;
        var client = _httpClientFactory.CreateClient("nya-llm");
        client.Timeout = TimeSpan.FromSeconds(Math.Max(30, profile.TimeoutSeconds));

        var body = BuildBody(pr);
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var msg = new HttpRequestMessage(HttpMethod.Post, NyaLlmSerialization.ResolveEndpoint(profile.BaseUrl)) { Content = content };
        if (!string.IsNullOrWhiteSpace(profile.ApiKey))
            msg.Headers.Add("x-api-key", profile.ApiKey);
        msg.Headers.Add("anthropic-version", AnthropicVersion);

        using var response = await client.SendAsync(msg, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[NyaLlm/Anthropic] {Status}: {Error}", response.StatusCode, NyaLlmSerialization.Truncate(raw, 200));
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        return ParseResponse(doc.RootElement);
    }

    /// <summary>純函式：NyaLlmProviderRequest → Anthropic request body。</summary>
    public static JsonObject BuildBody(NyaLlmProviderRequest pr)
    {
        var request = pr.Request;
        var body = new JsonObject
        {
            ["model"]       = request.Model,
            ["max_tokens"]  = request.MaxTokens ?? DefaultMaxTokens,
            ["temperature"] = request.Temperature,
            ["top_p"]       = request.TopP
        };

        NyaLlmSerialization.ApplyModelOverrides(body, pr.Profile); // 與 OpenAiChatProvider 一致：profile 的取樣覆寫寫入 body 頂層
        // 註：Anthropic 的 extended-thinking 形狀與共用 ApplyThinkingParams（enable_thinking/thinking_budget 扁平）不同，故此處刻意不套用；如需 thinking 另以 Anthropic 專屬 block 實作（後續）。

        var sys = string.Join("\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Content));
        if (!string.IsNullOrWhiteSpace(sys)) body["system"] = sys;

        var messages = new JsonArray();
        var pendingToolResults = new JsonArray();
        void FlushToolResults()
        {
            if (pendingToolResults.Count == 0) return;
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = pendingToolResults });
            pendingToolResults = new JsonArray();
        }

        foreach (var m in request.Messages)
        {
            if (m.Role == "system") continue;

            if (m.Role == "tool")
            {
                pendingToolResults.Add(new JsonObject
                {
                    ["type"]        = "tool_result",
                    ["tool_use_id"] = m.ToolCallId ?? "",
                    ["content"]     = m.Content
                });
                continue;
            }

            FlushToolResults();

            if (m.Role == "assistant" && m.ToolCalls?.Count > 0)
            {
                var blocks = new JsonArray();
                if (!string.IsNullOrEmpty(m.Content))
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
                foreach (var tc in m.ToolCalls)
                    blocks.Add(new JsonObject
                    {
                        ["type"]  = "tool_use",
                        ["id"]    = tc.Id,
                        ["name"]  = tc.FunctionName,
                        ["input"] = SafeParse(tc.FunctionArguments)
                    });
                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = blocks });
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
            }
        }
        FlushToolResults();
        body["messages"] = messages;

        if (request.Tools?.Count > 0)
        {
            var tools = new JsonArray();
            foreach (var t in request.Tools)
                tools.Add(new JsonObject
                {
                    ["name"]         = t.Function.Name,
                    ["description"]  = t.Function.Description,
                    ["input_schema"] = NyaLlmSerialization.BuildJsonSchema(t.Function.Parameters)
                });
            body["tools"] = tools;
        }

        return body;
    }

    /// <summary>純函式：Anthropic response root → NyaLlmResponse（tool_use → ToolCalls；否則 text）。</summary>
    public static NyaLlmResponse? ParseResponse(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return new NyaLlmResponse { Content = null };

        var toolCalls = new List<NyaToolCall>();
        var sb = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "tool_use")
            {
                var id = block.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var input = block.TryGetProperty("input", out var inp) ? inp.GetRawText() : "{}";
                toolCalls.Add(new NyaToolCall { Id = id, FunctionName = name, FunctionArguments = input });
            }
            else if (type == "text" && block.TryGetProperty("text", out var txt))
            {
                sb.Append(txt.GetString());
            }
        }

        if (toolCalls.Count > 0)
            return new NyaLlmResponse { ToolCalls = toolCalls };
        return new NyaLlmResponse { Content = NyaLlmSerialization.StripThinkTags(sb.ToString()) };
    }

    private static JsonNode SafeParse(string json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json) ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }
}
