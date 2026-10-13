using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.NyaChat;

/// <summary>
/// OpenAI Chat Completions provider（<c>POST v1/chat/completions</c>，含 function calling）。
/// 由原 <c>NyaLlmClient.SendChatCompletionsAsync</c> 搬出，行為等價，唯一變更：
/// 移除硬編碼 <c>if (model == "qwen3.6:35b-a3b-Q4")</c> hack，改由 profile 的
/// <see cref="NyaLlmProfile.ModelOverrides"/> 套用至 body 頂層。
/// 此為 <see cref="NyaLlmClient"/> 未知 provider 時的 fallback（維持原本 default 語意）。
/// </summary>
public sealed class OpenAiChatProvider : INyaLlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiChatProvider> _logger;

    public string Key => "openai_chat";

    public OpenAiChatProvider(IHttpClientFactory httpClientFactory, ILogger<OpenAiChatProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<NyaLlmResponse?> SendAsync(NyaLlmProviderRequest pr, CancellationToken ct)
    {
        var profile = pr.Profile;
        var request = pr.Request;

        var client = NyaLlmSerialization.CreateClient(_httpClientFactory, profile.BaseUrl, profile.ApiKey, profile.TimeoutSeconds);

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = NyaLlmSerialization.BuildJsonMessages(request.Messages),
            ["stream"] = false,
            ["temperature"] = request.Temperature,
            ["top_p"] = request.TopP
        };
        NyaLlmSerialization.ApplyModelOverrides(body, profile); // 取代硬編碼 hack（寫入 body 頂層）
        NyaLlmSerialization.ApplyThinkingParams(body, profile); // thinking：enable_thinking / thinking_budget（頂層）

        if (request.MaxTokens.HasValue)
            body["max_tokens"] = request.MaxTokens.Value;

        if (request.Tools?.Count > 0)
            body["tools"] = NyaLlmSerialization.BuildToolsJson(request.Tools);

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(NyaLlmSerialization.ResolveEndpoint(profile.BaseUrl), content, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[NyaLlm/Chat] {Status}: {Error}", response.StatusCode, NyaLlmSerialization.Truncate(raw, 200));
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        var choices = doc.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) return null;

        var choice = choices[0];
        var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
        var message = choice.GetProperty("message");

        // tool_calls
        if (finishReason == "tool_calls" &&
            message.TryGetProperty("tool_calls", out var tcs) &&
            tcs.ValueKind == JsonValueKind.Array &&
            tcs.GetArrayLength() > 0)
        {
            var toolCalls = NyaLlmSerialization.ParseToolCallsArray(tcs);
            if (toolCalls.Count > 0)
                return new NyaLlmResponse { ToolCalls = toolCalls };
        }

        var text = message.TryGetProperty("content", out var ct2) && ct2.ValueKind == JsonValueKind.String
            ? ct2.GetString() : null;
        return new NyaLlmResponse { Content = NyaLlmSerialization.StripThinkTags(text) };
    }
}
