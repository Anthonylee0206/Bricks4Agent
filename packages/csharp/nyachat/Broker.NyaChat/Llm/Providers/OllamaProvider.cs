using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.NyaChat;

/// <summary>
/// Ollama provider（<c>POST api/chat</c>）。
/// 由原 <c>NyaLlmClient.SendOllamaAsync</c> 搬出，行為等價，唯一變更：
/// 移除硬編碼 <c>if (model == "qwen3.6:35b-a3b-Q4")</c> hack，改由 profile 的
/// <see cref="NyaLlmProfile.ModelOverrides"/> 套用至 <c>options</c>。
/// </summary>
public sealed class OllamaProvider : INyaLlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OllamaProvider> _logger;

    public string Key => "ollama";

    public OllamaProvider(IHttpClientFactory httpClientFactory, ILogger<OllamaProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<NyaLlmResponse?> SendAsync(NyaLlmProviderRequest pr, CancellationToken ct)
    {
        var profile = pr.Profile;
        var request = pr.Request;

        var client = NyaLlmSerialization.CreateClient(_httpClientFactory, profile.BaseUrl, null, profile.TimeoutSeconds);

        var optionsObj = new JsonObject
        {
            ["temperature"] = request.Temperature,
            ["top_p"] = request.TopP
        };
        NyaLlmSerialization.ApplyModelOverrides(optionsObj, profile); // 取代硬編碼 hack（寫入 options）

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = NyaLlmSerialization.BuildJsonMessages(request.Messages),
            ["stream"] = false,
            ["options"] = optionsObj
        };
        if (request.MaxTokens.HasValue)
            body["options"]!.AsObject()["num_predict"] = request.MaxTokens.Value;

        // Ollama 支援 tools（與 OpenAI 相同格式）
        if (request.Tools?.Count > 0)
            body["tools"] = NyaLlmSerialization.BuildToolsJson(request.Tools);

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(NyaLlmSerialization.ResolveEndpoint(profile.BaseUrl), content, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[NyaLlm/Ollama] {Status}: {Error}", response.StatusCode, NyaLlmSerialization.Truncate(raw, 200));
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        var msg = doc.RootElement.GetProperty("message");
        return ParseOllamaMessage(msg);
    }

    private static NyaLlmResponse? ParseOllamaMessage(JsonElement msg)
    {
        // Check for tool_calls
        if (msg.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array && tcs.GetArrayLength() > 0)
        {
            var toolCalls = NyaLlmSerialization.ParseToolCallsArray(tcs);
            if (toolCalls.Count > 0)
                return new NyaLlmResponse { ToolCalls = toolCalls };
        }

        var text = msg.TryGetProperty("content", out var c) ? c.GetString() : null;
        return new NyaLlmResponse { Content = NyaLlmSerialization.StripThinkTags(text) };
    }
}
