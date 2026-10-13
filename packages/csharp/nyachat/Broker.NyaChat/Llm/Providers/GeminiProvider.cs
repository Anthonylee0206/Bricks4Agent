using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.NyaChat;

/// <summary>Google Gemini provider（<c>POST v1beta/models/{model}:generateContent</c>，含 function-calling，Doc 3 Plan B）。</summary>
public sealed class GeminiProvider : INyaLlmProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GeminiProvider> _logger;

    public string Key => "gemini";
    public bool SupportsTools => true;

    public GeminiProvider(IHttpClientFactory httpClientFactory, ILogger<GeminiProvider> logger)
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
            msg.Headers.Add("x-goog-api-key", profile.ApiKey);

        using var response = await client.SendAsync(msg, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("[NyaLlm/Gemini] {Status}: {Error}", response.StatusCode, NyaLlmSerialization.Truncate(raw, 200));
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        return ParseResponse(doc.RootElement);
    }

    /// <summary>純函式：NyaLlmProviderRequest → Gemini request body。</summary>
    public static JsonObject BuildBody(NyaLlmProviderRequest pr)
    {
        var request = pr.Request;
        var body = new JsonObject();

        var sys = string.Join("\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Content));
        if (!string.IsNullOrWhiteSpace(sys))
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = sys } } };

        var contents = new JsonArray();
        var pendingFnResponses = new JsonArray();
        void Flush()
        {
            if (pendingFnResponses.Count == 0) return;
            contents.Add(new JsonObject { ["role"] = "user", ["parts"] = pendingFnResponses });
            pendingFnResponses = new JsonArray();
        }

        foreach (var m in request.Messages)
        {
            if (m.Role == "system") continue;

            if (m.Role == "tool")
            {
                pendingFnResponses.Add(new JsonObject
                {
                    ["functionResponse"] = new JsonObject
                    {
                        ["name"]     = m.Name ?? "",
                        ["response"] = new JsonObject { ["content"] = m.Content }
                    }
                });
                continue;
            }

            Flush();

            if (m.Role == "assistant" && m.ToolCalls?.Count > 0)
            {
                var parts = new JsonArray();
                if (!string.IsNullOrEmpty(m.Content)) parts.Add(new JsonObject { ["text"] = m.Content });
                foreach (var tc in m.ToolCalls)
                    parts.Add(new JsonObject
                    {
                        ["functionCall"] = new JsonObject { ["name"] = tc.FunctionName, ["args"] = SafeParse(tc.FunctionArguments) }
                    });
                contents.Add(new JsonObject { ["role"] = "model", ["parts"] = parts });
            }
            else
            {
                var role = m.Role == "assistant" ? "model" : "user";
                contents.Add(new JsonObject { ["role"] = role, ["parts"] = new JsonArray { new JsonObject { ["text"] = m.Content } } });
            }
        }
        Flush();
        body["contents"] = contents;

        if (request.Tools?.Count > 0)
        {
            var decls = new JsonArray();
            foreach (var t in request.Tools)
                decls.Add(new JsonObject
                {
                    ["name"]        = t.Function.Name,
                    ["description"] = t.Function.Description,
                    ["parameters"]  = NyaLlmSerialization.BuildJsonSchema(t.Function.Parameters)
                });
            body["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = decls } };
        }

        var genCfg = new JsonObject { ["temperature"] = request.Temperature, ["topP"] = request.TopP };
        if (request.MaxTokens.HasValue) genCfg["maxOutputTokens"] = request.MaxTokens.Value;
        // profile 取樣覆寫寫入 generationConfig（Gemini 的取樣參數落點）。
        // 註：Gemini 的 thinking（thinkingConfig）形狀與共用 ApplyThinkingParams 不同，故此處不套用 thinking helper；如需另以 Gemini 專屬實作（後續）。
        NyaLlmSerialization.ApplyModelOverrides(genCfg, pr.Profile);
        body["generationConfig"] = genCfg;

        return body;
    }

    /// <summary>純函式：Gemini response root → NyaLlmResponse。</summary>
    public static NyaLlmResponse? ParseResponse(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
            return null;
        if (!cands[0].TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
            return new NyaLlmResponse { Content = null };

        var toolCalls = new List<NyaToolCall>();
        var sb = new StringBuilder();
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("functionCall", out var fc))
            {
                var name = fc.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var args = fc.TryGetProperty("args", out var a) ? a.GetRawText() : "{}";
                toolCalls.Add(new NyaToolCall { Id = Guid.NewGuid().ToString("N")[..8], FunctionName = name, FunctionArguments = args });
            }
            else if (part.TryGetProperty("text", out var txt))
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
