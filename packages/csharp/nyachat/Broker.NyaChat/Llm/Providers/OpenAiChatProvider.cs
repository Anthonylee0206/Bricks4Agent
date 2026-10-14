using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.NyaChat;

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

        var body = NyaLlmAdminSettings.Sampling(profile, request);
        body["model"] = request.Model;
        body["messages"] = NyaLlmSerialization.BuildJsonMessages(request.Messages);
        body["stream"] = false;
        NyaLlmSerialization.ApplyThinkingParams(body, profile);

        if (request.MaxTokens.HasValue)
            body["max_tokens"] = request.MaxTokens.Value;

        if (request.Tools?.Count > 0)
            body["tools"] = NyaLlmSerialization.BuildToolsJson(request.Tools);

        MergeExtraBody(body, profile.ExtraBody);

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(NyaLlmSerialization.ResolveEndpoint(profile.BaseUrl), content, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[NyaLlm/Chat] {Status}: {Error}", response.StatusCode, NyaLlmSerialization.Truncate(raw, 200));
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            _logger.LogError("[NyaLlm/Chat] response without choices: {Raw}", NyaLlmSerialization.Truncate(raw, 500));
            return null;
        }
        if (choices.GetArrayLength() == 0) return null;

        var choice = choices[0];
        var finishReason = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
        var message = choice.GetProperty("message");

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
    public static void MergeExtraBody(JsonObject body, JsonObject? extra)
    {
        if (extra == null) return;
        if (extra.Any(p => p.Key is "model" or "messages" or "tools" or "stream"))
            throw new InvalidOperationException("extra_body 不可覆寫保留欄位。");
        MergeObject(body, extra);
    }

    private static void MergeObject(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
            if (value is JsonObject nested && target[key] is JsonObject existing) MergeObject(existing, nested);
            else target[key] = value?.DeepClone();
    }

}
