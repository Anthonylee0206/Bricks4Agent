using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.NyaChat;

public static class NyaLlmSerialization
{

    public static HttpClient CreateClient(
        IHttpClientFactory httpClientFactory, string baseUrl, string? apiKey, int timeoutSeconds)
    {
        var client = httpClientFactory.CreateClient("nya-llm");
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromSeconds(Math.Max(30, timeoutSeconds));
        if (!string.IsNullOrWhiteSpace(apiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    public static Uri ResolveEndpoint(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("API 位置不可為空", nameof(baseUrl));
        return new Uri(baseUrl.Trim(), UriKind.Absolute);
    }

    public static void ApplyModelOverrides(JsonObject target, NyaLlmProfile profile)
    {
        if (profile.ModelOverrides is null) return;
        foreach (var (key, value) in profile.ModelOverrides)
            target[key] = JsonValue.Create(value);
    }

    public static void ApplyThinkingParams(JsonObject target, NyaLlmProfile profile)
    {
        if (profile.EnableThinking.HasValue)
            target["enable_thinking"] = profile.EnableThinking.Value;
        if (profile.ThinkingBudget.HasValue)
            target["thinking_budget"] = profile.ThinkingBudget.Value;
    }

    public static JsonArray BuildJsonMessages(List<NyaLlmMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            var obj = new JsonObject { ["role"] = m.Role };

            if (m.ToolCalls?.Count > 0)
            {

                obj["content"] = m.Content.Length > 0 ? (JsonNode)JsonValue.Create(m.Content)! : null;
                var tca = new JsonArray();
                foreach (var tc in m.ToolCalls)
                {
                    tca.Add(new JsonObject
                    {
                        ["id"]   = tc.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"]      = tc.FunctionName,
                            ["arguments"] = tc.FunctionArguments
                        }
                    });
                }
                obj["tool_calls"] = tca;
            }
            else if (m.Role == "tool")
            {

                obj["content"] = m.Content;
                if (m.ToolCallId != null) obj["tool_call_id"] = m.ToolCallId;
                if (m.Name != null)       obj["name"]         = m.Name;
            }
            else
            {
                obj["content"] = m.Content;
            }

            arr.Add(obj);
        }
        return arr;
    }

    public static JsonArray BuildToolsJson(List<NyaLlmTool> tools)
    {
        var arr = new JsonArray();
        foreach (var tool in tools)
        {
            var paramNode = BuildJsonSchema(tool.Function.Parameters);

            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"]        = tool.Function.Name,
                    ["description"] = tool.Function.Description,
                    ["parameters"]  = paramNode
                }
            });
        }
        return arr;
    }

    public static List<NyaToolCall> ParseToolCallsArray(JsonElement tcs)
    {
        var list = new List<NyaToolCall>();
        foreach (var tc in tcs.EnumerateArray())
        {

            if (!tc.TryGetProperty("function", out var fn)) continue;

            var name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string argsStr;
            if (fn.TryGetProperty("arguments", out var args))
            {
                argsStr = args.ValueKind == JsonValueKind.String
                    ? args.GetString() ?? "{}"
                    : args.GetRawText();
            }
            else argsStr = "{}";

            var id = tc.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? Guid.NewGuid().ToString("N")[..8] : Guid.NewGuid().ToString("N")[..8];

            list.Add(new NyaToolCall { Id = id, FunctionName = name, FunctionArguments = argsStr });
        }
        return list;
    }

    public static string? StripThinkTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text?.Trim();
        if (text.Contains("<think>", StringComparison.Ordinal))
        {
            var end = text.IndexOf("</think>", StringComparison.Ordinal);
            if (end >= 0) text = text[(end + 8)..].TrimStart();
        }
        return text.Trim();
    }

    public static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    public static JsonObject BuildJsonSchema(NyaLlmFunctionParameters parameters)
    {
        var propsObj = new JsonObject();
        foreach (var (name, prop) in parameters.Properties) propsObj[name] = BuildProperty(prop);
        var schema = new JsonObject { ["type"] = "object", ["properties"] = propsObj, ["additionalProperties"] = parameters.AdditionalProperties };
        if (parameters.RulesJson != null)
            foreach (var rule in JsonNode.Parse(parameters.RulesJson)!.AsObject()) schema[rule.Key] = rule.Value?.DeepClone();
        if (parameters.Required.Count > 0)
        {
            var req = new JsonArray();
            foreach (var r in parameters.Required) req.Add(r);
            schema["required"] = req;
        }
        return schema;
    }
    private static JsonObject BuildProperty(NyaLlmParameterProperty prop)
    {
        var node = new JsonObject { ["type"] = prop.Type, ["description"] = prop.Description };
        if (prop.Enum != null) node["enum"] = JsonNode.Parse(prop.Enum);
        if (prop.MinLength.HasValue) node["minLength"] = prop.MinLength.Value;
        if (prop.MaxLength.HasValue) node["maxLength"] = prop.MaxLength.Value;
        if (prop.Minimum.HasValue) node["minimum"] = prop.Minimum.Value;
        if (prop.Maximum.HasValue) node["maximum"] = prop.Maximum.Value;
        if (prop.MinItems.HasValue) node["minItems"] = prop.MinItems.Value;
        if (prop.MaxItems.HasValue) node["maxItems"] = prop.MaxItems.Value;
        if (prop.Pattern != null) node["pattern"] = prop.Pattern;
        if (prop.Format != null) node["format"] = prop.Format;
        if (prop.Items != null) node["items"] = BuildProperty(prop.Items);
        if (prop.Type == "object")
        {
            node["additionalProperties"] = false;
            var properties = new JsonObject();
            foreach (var (name, property) in prop.Properties) properties[name] = BuildProperty(property);
            node["properties"] = properties;
            node["required"] = JsonSerializer.SerializeToNode(prop.Required);
            if (prop.RulesJson != null)
                foreach (var rule in JsonNode.Parse(prop.RulesJson)!.AsObject()) node[rule.Key] = rule.Value?.DeepClone();
        }
        return node;
    }
}
