using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.NyaChat;

public sealed class OpenAiResponsesProvider : INyaLlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiResponsesProvider> _logger;

    public string Key => "openai_responses";
    public bool SupportsTools => true;

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
        foreach (var message in request.Messages)
        {
            if (message.ToolCalls is { Count: > 0 })
            {
                foreach (var call in message.ToolCalls)
                    input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = call.Id, ["name"] = call.FunctionName, ["arguments"] = call.FunctionArguments });
            }
            else if (message.Role == "tool")
                input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = message.ToolCallId, ["output"] = message.Content });
            else
                input.Add(new JsonObject
                {
                    ["role"] = message.Role,
                    ["content"] = new JsonArray { new JsonObject { ["type"] = message.Role == "assistant" ? "output_text" : "input_text", ["text"] = message.Content } }
                });
        }
        var body = new JsonObject { ["model"] = request.Model, ["input"] = input, ["stream"] = false };
        if (request.MaxTokens.HasValue) body["max_output_tokens"] = request.MaxTokens.Value;
        if (request.Tools is { Count: > 0 })
        {
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
                tools.Add(new JsonObject { ["type"] = "function", ["name"] = tool.Function.Name, ["description"] = tool.Function.Description, ["parameters"] = NyaLlmSerialization.BuildJsonSchema(tool.Function.Parameters) });
            body["tools"] = tools;
        }
        NyaLlmSerialization.ApplyThinkingParams(body, profile);
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(NyaLlmSerialization.ResolveEndpoint(profile.BaseUrl), content, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[NyaLlm/Responses] {Status}: {Error}", response.StatusCode, NyaLlmSerialization.Truncate(raw, 200));
            return null;
        }
        using var doc = JsonDocument.Parse(raw);
        var texts = new List<string>();
        var calls = new List<NyaToolCall>();
        var hasOutputText = doc.RootElement.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String;
        if (hasOutputText) texts.Add(outputText.GetString()!);
        if (doc.RootElement.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) && type.GetString() == "function_call")
                    calls.Add(new NyaToolCall { Id = item.GetProperty("call_id").GetString()!, FunctionName = item.GetProperty("name").GetString()!, FunctionArguments = item.GetProperty("arguments").GetString()! });
                else if (!hasOutputText && item.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array)
                    foreach (var part in parts.EnumerateArray())
                        if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String) texts.Add(text.GetString()!);
            }
        }
        return calls.Count + texts.Count == 0 ? null : new NyaLlmResponse { Content = NyaLlmSerialization.StripThinkTags(string.Join("\n", texts)), ToolCalls = calls.Count == 0 ? null : calls };
    }
}
