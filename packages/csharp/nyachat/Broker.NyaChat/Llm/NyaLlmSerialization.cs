using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.NyaChat;

/// <summary>
/// LLM wire-format 共用層。三個 provider 共用同一份序列化，確保
/// <b>role=tool / assistant(+tool_calls) 的閉環序列化在所有 provider 一致</b>——
/// 此為工具閉環契約，重構 provider 時<b>逐位元保留、不得弄丟</b>。
/// </summary>
/// <remarks>
/// 內容由原 <c>NyaLlmClient</c> 的 private static 方法<b>原樣抽出</b>（行為等價），加上
/// <see cref="ApplyModelOverrides"/>（2C：取代硬編碼模型 hack）。
/// </remarks>
public static class NyaLlmSerialization
{
    // ── HTTP client ─────────────────────────────────────────────────────────

    /// <summary>建立指向 <paramref name="baseUrl"/> 的命名 HttpClient（沿用 "nya-llm"）。</summary>
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

    /// <summary>
    /// 解析使用者在管理介面儲存的「API 位置」為請求端點 <see cref="Uri"/>——<b>原樣使用：不附加任何子路徑、
    /// 不改寫 path</b>。回傳絕對 URI，呼叫端傳給 <c>HttpClient</c>（蓋過 BaseAddress）。
    /// </summary>
    /// <remarks>
    /// 契約：profile 的 <c>BaseUrl</c> 即<b>完整端點</b>（如 <c>http://localhost:1234/v1/responses</c>、
    /// <c>https://api.openai.com/v1/chat/completions</c>）。先前各 provider 會在儲存值之後再附加固定子路徑
    /// （<c>v1/chat/completions</c> 等），使用者把含 <c>/v1</c> 的位置存進去時就被加倍成 <c>/v1/v1/...</c> 而 404。
    /// 改為直接讀取儲存值——使用者輸入什麼就打什麼，後端不做任何 URL 調整。
    /// </remarks>
    public static Uri ResolveEndpoint(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new ArgumentException("API 位置不可為空", nameof(baseUrl));
        return new Uri(baseUrl.Trim(), UriKind.Absolute);
    }

    // ── 模型特定覆寫（2C）─────────────────────────────────────────────────────

    /// <summary>
    /// 將 profile 的扁平取樣覆寫寫入 <paramref name="target"/>（覆寫該 profile 模型的預設取樣）。
    /// ollama 傳入 <c>options</c> 物件、openai_chat 傳入 body 頂層——落點由各 provider 決定。
    /// </summary>
    public static void ApplyModelOverrides(JsonObject target, NyaLlmProfile profile)
    {
        if (profile.ModelOverrides is null) return;
        foreach (var (key, value) in profile.ModelOverrides)
            target[key] = JsonValue.Create(value); // double → JSON number（STJ 會把 20.0 輸出為 20、1.5 為 1.5）
    }

    /// <summary>
    /// 將 profile 的思考（thinking）參數寫入 body 頂層（對應 OpenAI Python SDK 的 extra_body）：
    /// <c>enable_thinking</c> / <c>thinking_budget</c>。null 欄位不送；端點不認得會忽略未知欄位。
    /// 用於 Qwen3 / DashScope 相容端點，設 enable_thinking=false 或縮小 budget 可加快回覆。
    /// </summary>
    public static void ApplyThinkingParams(JsonObject target, NyaLlmProfile profile)
    {
        if (profile.EnableThinking.HasValue)
            target["enable_thinking"] = profile.EnableThinking.Value;
        if (profile.ThinkingBudget.HasValue)
            target["thinking_budget"] = profile.ThinkingBudget.Value;
    }

    // ── 訊息序列化（閉環契約）─────────────────────────────────────────────────

    /// <summary>
    /// 將統一訊息格式翻譯為 OpenAI/Ollama 相容的 messages 陣列。
    /// <b>assistant(+tool_calls) 與 tool(+tool_call_id/name) 分支是工具閉環契約。</b>
    /// </summary>
    public static JsonArray BuildJsonMessages(List<NyaLlmMessage> messages)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            var obj = new JsonObject { ["role"] = m.Role };

            if (m.ToolCalls?.Count > 0)
            {
                // assistant 訊息含 tool_calls
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
                // tool result 訊息
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

    /// <summary>將工具定義序列化為 OpenAI/Ollama function-calling 的 tools 陣列（含 enum）。</summary>
    public static JsonArray BuildToolsJson(List<NyaLlmTool> tools)
    {
        var arr = new JsonArray();
        foreach (var tool in tools)
        {
            var propsObj = new JsonObject();
            foreach (var (name, prop) in tool.Function.Parameters.Properties)
            {
                var propNode = new JsonObject
                {
                    ["type"]        = prop.Type,
                    ["description"] = prop.Description
                };
                if (prop.Enum != null)
                    propNode["enum"] = JsonNode.Parse(prop.Enum);
                propsObj[name] = propNode;
            }

            var paramNode = new JsonObject
            {
                ["type"]       = "object",
                ["properties"] = propsObj
            };
            if (tool.Function.Parameters.Required.Count > 0)
            {
                var req = new JsonArray();
                foreach (var r in tool.Function.Parameters.Required) req.Add(r);
                paramNode["required"] = req;
            }

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

    /// <summary>解析 tool_calls 陣列（相容 Ollama 物件 arguments 與 OpenAI 字串 arguments 兩種形狀）。</summary>
    public static List<NyaToolCall> ParseToolCallsArray(JsonElement tcs)
    {
        var list = new List<NyaToolCall>();
        foreach (var tc in tcs.EnumerateArray())
        {
            // Ollama 格式: { "function": { "name": "...", "arguments": {} } }
            // OpenAI 格式: { "id": "...", "type": "function", "function": { "name": "...", "arguments": "..." } }
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

    // ── 文字後處理 ────────────────────────────────────────────────────────────

    /// <summary>移除 reasoning 模型輸出的 &lt;think&gt;…&lt;/think&gt; 段落。</summary>
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

    /// <summary>
    /// 建構「裸」JSON-Schema 物件（type:object + properties + required + enum），
    /// 供 Anthropic <c>input_schema</c> 與 Gemini <c>parameters</c> 共用（兩者皆吃標準 JSON-Schema，
    /// 不像 OpenAI 還包一層 {type:function,function:{...}}）。
    /// </summary>
    public static JsonObject BuildJsonSchema(NyaLlmFunctionParameters parameters)
    {
        var propsObj = new JsonObject();
        foreach (var (name, prop) in parameters.Properties)
        {
            var propNode = new JsonObject { ["type"] = prop.Type, ["description"] = prop.Description };
            if (prop.Enum != null) propNode["enum"] = JsonNode.Parse(prop.Enum);
            propsObj[name] = propNode;
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = propsObj };
        if (parameters.Required.Count > 0)
        {
            var req = new JsonArray();
            foreach (var r in parameters.Required) req.Add(r);
            schema["required"] = req;
        }
        return schema;
    }
}
