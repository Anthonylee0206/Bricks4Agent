using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.NyaChat;

/// <summary>
/// OpenAI Responses API provider（<c>POST v1/responses</c>）。
/// 由原 <c>NyaLlmClient.SendResponsesApiAsync</c> 原樣搬出。
/// </summary>
/// <remarks>
/// <b>既有限制（不回歸亦不擴張）</b>：此 path <b>不支援 function calling</b>——不送 tools、
/// 不序列化 <c>role=tool</c>。因此工具閉環在此 provider 不可用，與遷移前一致。
/// 若需在 Responses API 上支援工具，屬獨立後續工作。
/// </remarks>
public sealed class OpenAiResponsesProvider : INyaLlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiResponsesProvider> _logger;

    public string Key => "openai_responses";

    /// <summary>Responses API path 不送 tools、不序列化 role=tool → 不支援工具閉環（見類別 remarks）。</summary>
    public bool SupportsTools => false;

    public OpenAiResponsesProvider(IHttpClientFactory httpClientFactory, ILogger<OpenAiResponsesProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<NyaLlmResponse?> SendAsync(NyaLlmProviderRequest pr, CancellationToken ct)
    {
        var profile = pr.Profile;
        var request = pr.Request;

        var client = NyaLlmSerialization.CreateClient(_httpClientFactory, profile.BaseUrl, profile.ApiKey, profile.TimeoutSeconds);

        var input = new JsonArray();
        foreach (var msg in request.Messages)
        {
            var role = msg.Role;
            var contentType = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "output_text" : "input_text";
            input.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = contentType, ["text"] = msg.Content }
                }
            });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["input"] = input,
            ["stream"] = false
        };
        NyaLlmSerialization.ApplyThinkingParams(body, profile); // thinking：enable_thinking / thinking_budget（頂層）

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(NyaLlmSerialization.ResolveEndpoint(profile.BaseUrl), content, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[NyaLlm/Responses] {Status}: {Error}", response.StatusCode, NyaLlmSerialization.Truncate(raw, 200));
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        if (doc.RootElement.TryGetProperty("output_text", out var ot) && ot.ValueKind == JsonValueKind.String)
            return new NyaLlmResponse { Content = NyaLlmSerialization.StripThinkTags(ot.GetString()) };

        if (doc.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var ca) || ca.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var ci in ca.EnumerateArray())
                {
                    if (ci.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        return new NyaLlmResponse { Content = NyaLlmSerialization.StripThinkTags(t.GetString()) };
                }
            }
        }

        return null;
    }
}
