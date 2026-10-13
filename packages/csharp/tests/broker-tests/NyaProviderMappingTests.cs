using System.Text.Json;
using System.Text.Json.Nodes;
using Broker.NyaChat;

namespace Broker.Tests;

/// <summary>
/// Doc 3 Plan B：原生 provider（Anthropic / Gemini）的 wire-format 純函式映射測試。
/// 只測 BuildBody/ParseResponse 與共用 schema helper；HTTP 不在單測。
/// </summary>
public static class NyaProviderMappingTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0; _failed = 0;
        Console.WriteLine("=== NyaChat Provider Mapping Tests (Doc 3 Plan B) ===");
        Console.WriteLine();

        TestBuildJsonSchema();
        TestResolveEndpointUsesStoredUrlVerbatim();
        TestAnthropicTextBody();
        TestAnthropicSystemExtraction();
        TestAnthropicToolsAndToolLoop();
        TestAnthropicParseText();
        TestAnthropicParseToolUse();
        TestAnthropicAppliesModelOverrides();
        TestGeminiTextBody();
        TestGeminiSystemInstruction();
        TestGeminiToolLoop();
        TestGeminiParseText();
        TestGeminiParseFunctionCall();
        TestGeminiAppliesModelOverrides();

        Console.WriteLine();
        Console.WriteLine($"=== Provider Mapping Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static NyaLlmProviderRequest Pr(NyaLlmRequest req)
        => new() { Profile = new NyaLlmProfile { Provider = "anthropic", Model = "claude-x", ApiKey = "k", BaseUrl = "https://api.anthropic.com" }, Request = req };

    private static void TestAnthropicTextBody()
    {
        Console.WriteLine("--- anthropic BuildBody: text request maps model/max_tokens/messages ---");
        var req = new NyaLlmRequest { Model = "claude-x", Temperature = 0.4f, TopP = 0.9f, MaxTokens = 100,
            Messages = new() { new() { Role = "user", Content = "hi" } } };
        var body = AnthropicProvider.BuildBody(Pr(req));
        AssertEqual("a-model", "claude-x", body["model"]!.GetValue<string>());
        AssertEqual("a-maxtokens", 100, body["max_tokens"]!.GetValue<int>());
        var msgs = body["messages"]!.AsArray();
        AssertEqual("a-msg-role", "user", msgs[0]!.AsObject()["role"]!.GetValue<string>());
        AssertEqual("a-msg-content", "hi", msgs[0]!.AsObject()["content"]!.GetValue<string>());
        AssertTrue("a-no-system", body["system"] == null);
    }

    private static void TestAnthropicSystemExtraction()
    {
        Console.WriteLine("--- anthropic BuildBody: system messages hoisted to top-level, default max_tokens ---");
        var req = new NyaLlmRequest { Messages = new()
            { new() { Role = "system", Content = "you are nya" }, new() { Role = "user", Content = "hi" } } };
        var body = AnthropicProvider.BuildBody(Pr(req));
        AssertEqual("a-system", "you are nya", body["system"]!.GetValue<string>());
        AssertEqual("a-default-maxtokens", 4096, body["max_tokens"]!.GetValue<int>());
        AssertEqual("a-msg-count", 1, body["messages"]!.AsArray().Count);
    }

    private static void TestAnthropicToolsAndToolLoop()
    {
        Console.WriteLine("--- anthropic BuildBody: tools schema + assistant tool_use + merged tool_result user turn ---");
        var req = new NyaLlmRequest
        {
            Tools = new() { SampleTool() },
            Messages = new()
            {
                new() { Role = "user", Content = "weather?" },
                new() { Role = "assistant", Content = "", ToolCalls = new() {
                    new() { Id = "tu_1", FunctionName = "get_weather", FunctionArguments = "{\"city\":\"taipei\"}" } } },
                new() { Role = "tool", ToolCallId = "tu_1", Name = "get_weather", Content = "27c" }
            }
        };
        var body = AnthropicProvider.BuildBody(Pr(req));
        AssertEqual("a-tool-name", "get_weather", body["tools"]!.AsArray()[0]!.AsObject()["name"]!.GetValue<string>());
        AssertTrue("a-tool-schema", body["tools"]!.AsArray()[0]!.AsObject()["input_schema"] != null);
        var msgs = body["messages"]!.AsArray();
        var asst = msgs[1]!.AsObject();
        AssertEqual("a-asst-role", "assistant", asst["role"]!.GetValue<string>());
        var block = asst["content"]!.AsArray()[0]!.AsObject();
        AssertEqual("a-tooluse-type", "tool_use", block["type"]!.GetValue<string>());
        AssertEqual("a-tooluse-id", "tu_1", block["id"]!.GetValue<string>());
        AssertEqual("a-tooluse-city", "taipei", block["input"]!.AsObject()["city"]!.GetValue<string>());
        var toolTurn = msgs[2]!.AsObject();
        AssertEqual("a-result-role", "user", toolTurn["role"]!.GetValue<string>());
        var rblock = toolTurn["content"]!.AsArray()[0]!.AsObject();
        AssertEqual("a-result-type", "tool_result", rblock["type"]!.GetValue<string>());
        AssertEqual("a-result-id", "tu_1", rblock["tool_use_id"]!.GetValue<string>());
        AssertEqual("a-result-content", "27c", rblock["content"]!.GetValue<string>());
    }

    private static void TestAnthropicParseText()
    {
        Console.WriteLine("--- anthropic ParseResponse: text blocks -> Content ---");
        var json = "{\"content\":[{\"type\":\"text\",\"text\":\"hello\"}],\"stop_reason\":\"end_turn\"}";
        using var doc = JsonDocument.Parse(json);
        var resp = AnthropicProvider.ParseResponse(doc.RootElement);
        AssertTrue("a-parse-not-null", resp != null);
        AssertEqual("a-parse-content", "hello", resp!.Content);
        AssertTrue("a-parse-no-tools", !resp.HasToolCalls);
    }

    private static void TestAnthropicParseToolUse()
    {
        Console.WriteLine("--- anthropic ParseResponse: tool_use blocks -> ToolCalls ---");
        var json = "{\"content\":[{\"type\":\"tool_use\",\"id\":\"tu_9\",\"name\":\"get_weather\",\"input\":{\"city\":\"taipei\"}}],\"stop_reason\":\"tool_use\"}";
        using var doc = JsonDocument.Parse(json);
        var resp = AnthropicProvider.ParseResponse(doc.RootElement);
        AssertTrue("a-parse-has-tools", resp != null && resp.HasToolCalls);
        AssertEqual("a-parse-tool-name", "get_weather", resp!.ToolCalls![0].FunctionName);
        AssertTrue("a-parse-tool-args", resp.ToolCalls![0].FunctionArguments.Contains("taipei"));
    }

    private static void TestAnthropicAppliesModelOverrides()
    {
        Console.WriteLine("--- anthropic BuildBody: profile ModelOverrides written to body top-level ---");
        var pr = new NyaLlmProviderRequest
        {
            Profile = new NyaLlmProfile { Provider = "anthropic", Model = "claude-x", ApiKey = "k",
                BaseUrl = "https://api.anthropic.com", ModelOverrides = new() { ["top_k"] = 40 } },
            Request = new NyaLlmRequest { Model = "claude-x", Messages = new() { new() { Role = "user", Content = "hi" } } }
        };
        var body = AnthropicProvider.BuildBody(pr);
        AssertEqual("a-override-top_k", 40d, body["top_k"]!.GetValue<double>());
    }

    private static NyaLlmProviderRequest GPr(NyaLlmRequest req)
        => new() { Profile = new NyaLlmProfile { Provider = "gemini", Model = "gemini-x", ApiKey = "k", BaseUrl = "https://generativelanguage.googleapis.com" }, Request = req };

    private static void TestGeminiTextBody()
    {
        Console.WriteLine("--- gemini BuildBody: user text -> contents[user/text] + generationConfig ---");
        var req = new NyaLlmRequest { Model = "gemini-x", Temperature = 0.5f, TopP = 0.8f, MaxTokens = 200,
            Messages = new() { new() { Role = "user", Content = "hi" } } };
        var body = GeminiProvider.BuildBody(GPr(req));
        var c0 = body["contents"]!.AsArray()[0]!.AsObject();
        AssertEqual("g-role", "user", c0["role"]!.GetValue<string>());
        AssertEqual("g-text", "hi", c0["parts"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>());
        AssertEqual("g-temp", 0.5f, body["generationConfig"]!.AsObject()["temperature"]!.GetValue<float>());
        AssertEqual("g-maxout", 200, body["generationConfig"]!.AsObject()["maxOutputTokens"]!.GetValue<int>());
    }

    private static void TestGeminiSystemInstruction()
    {
        Console.WriteLine("--- gemini BuildBody: system -> systemInstruction, not in contents ---");
        var req = new NyaLlmRequest { Messages = new()
            { new() { Role = "system", Content = "sys" }, new() { Role = "user", Content = "hi" } } };
        var body = GeminiProvider.BuildBody(GPr(req));
        AssertEqual("g-sysinstr", "sys", body["systemInstruction"]!.AsObject()["parts"]!.AsArray()[0]!.AsObject()["text"]!.GetValue<string>());
        AssertEqual("g-contents-count", 1, body["contents"]!.AsArray().Count);
    }

    private static void TestGeminiToolLoop()
    {
        Console.WriteLine("--- gemini BuildBody: assistant functionCall (model) + merged functionResponse (user) ---");
        var req = new NyaLlmRequest
        {
            Tools = new() { SampleTool() },
            Messages = new()
            {
                new() { Role = "user", Content = "weather?" },
                new() { Role = "assistant", Content = "", ToolCalls = new() {
                    new() { Id = "x", FunctionName = "get_weather", FunctionArguments = "{\"city\":\"taipei\"}" } } },
                new() { Role = "tool", ToolCallId = "x", Name = "get_weather", Content = "27c" }
            }
        };
        var body = GeminiProvider.BuildBody(GPr(req));
        AssertTrue("g-tools-decl", body["tools"]!.AsArray()[0]!.AsObject()["functionDeclarations"] != null);
        var contents = body["contents"]!.AsArray();
        var model = contents[1]!.AsObject();
        AssertEqual("g-model-role", "model", model["role"]!.GetValue<string>());
        var fc = model["parts"]!.AsArray()[0]!.AsObject()["functionCall"]!.AsObject();
        AssertEqual("g-fc-name", "get_weather", fc["name"]!.GetValue<string>());
        AssertEqual("g-fc-city", "taipei", fc["args"]!.AsObject()["city"]!.GetValue<string>());
        var toolTurn = contents[2]!.AsObject();
        AssertEqual("g-fr-role", "user", toolTurn["role"]!.GetValue<string>());
        var fr = toolTurn["parts"]!.AsArray()[0]!.AsObject()["functionResponse"]!.AsObject();
        AssertEqual("g-fr-name", "get_weather", fr["name"]!.GetValue<string>());
    }

    private static void TestGeminiParseText()
    {
        Console.WriteLine("--- gemini ParseResponse: text part -> Content ---");
        var json = "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"hello\"}]}}]}";
        using var doc = JsonDocument.Parse(json);
        var resp = GeminiProvider.ParseResponse(doc.RootElement);
        AssertEqual("g-parse-content", "hello", resp!.Content);
    }

    private static void TestGeminiParseFunctionCall()
    {
        Console.WriteLine("--- gemini ParseResponse: functionCall -> ToolCalls ---");
        var json = "{\"candidates\":[{\"content\":{\"parts\":[{\"functionCall\":{\"name\":\"get_weather\",\"args\":{\"city\":\"taipei\"}}}]}}]}";
        using var doc = JsonDocument.Parse(json);
        var resp = GeminiProvider.ParseResponse(doc.RootElement);
        AssertTrue("g-parse-has-tools", resp != null && resp.HasToolCalls);
        AssertEqual("g-parse-name", "get_weather", resp!.ToolCalls![0].FunctionName);
        AssertTrue("g-parse-args", resp.ToolCalls![0].FunctionArguments.Contains("taipei"));
    }

    private static void TestGeminiAppliesModelOverrides()
    {
        Console.WriteLine("--- gemini BuildBody: ModelOverrides written into generationConfig ---");
        var pr = new NyaLlmProviderRequest
        {
            Profile = new NyaLlmProfile { Provider = "gemini", Model = "gemini-x", ApiKey = "k",
                BaseUrl = "https://x", ModelOverrides = new() { ["topK"] = 40 } },
            Request = new NyaLlmRequest { Model = "gemini-x", Messages = new() { new() { Role = "user", Content = "hi" } } }
        };
        var body = GeminiProvider.BuildBody(pr);
        AssertEqual("g-override-topK", 40d, body["generationConfig"]!.AsObject()["topK"]!.GetValue<double>());
    }

    private static NyaLlmTool SampleTool() => new()
    {
        Function = new NyaLlmFunction
        {
            Name = "get_weather", Description = "查天氣",
            Parameters = new NyaLlmFunctionParameters
            {
                Properties = new()
                {
                    ["city"] = new() { Type = "string", Description = "城市" },
                    ["unit"] = new() { Type = "string", Description = "單位", Enum = "[\"c\",\"f\"]" }
                },
                Required = new() { "city" }
            }
        }
    };

    private static void TestResolveEndpointUsesStoredUrlVerbatim()
    {
        Console.WriteLine("--- ResolveEndpoint: 直接使用儲存的 API 位置，不附加子路徑、不改寫 path ---");
        // 使用者儲存完整端點 → 原樣送出（不再被附加 v1/responses、v1beta/... 等）。
        AssertEqual("e-responses", "http://localhost:1234/v1/responses",
            NyaLlmSerialization.ResolveEndpoint("http://localhost:1234/v1/responses").AbsoluteUri);
        AssertEqual("e-chat", "http://localhost:1234/v1/chat/completions",
            NyaLlmSerialization.ResolveEndpoint("http://localhost:1234/v1/chat/completions").AbsoluteUri);
        AssertEqual("e-ollama", "http://localhost:11434/api/chat",
            NyaLlmSerialization.ResolveEndpoint("http://localhost:11434/api/chat").AbsoluteUri);
        // 前後空白容錯（純解析，非 URL 改寫）。
        AssertEqual("e-trim", "http://localhost:1234/v1/chat/completions",
            NyaLlmSerialization.ResolveEndpoint("  http://localhost:1234/v1/chat/completions  ").AbsoluteUri);
        // 空字串 → 明確拋例外（mis-config 守衛，不靜默打到錯地方）。
        var threw = false;
        try { NyaLlmSerialization.ResolveEndpoint(" "); } catch (ArgumentException) { threw = true; }
        AssertTrue("e-empty-throws", threw);
    }

    private static void TestBuildJsonSchema()
    {
        Console.WriteLine("--- BuildJsonSchema: bare object schema with properties/required/enum ---");
        var schema = NyaLlmSerialization.BuildJsonSchema(SampleTool().Function.Parameters);
        AssertEqual("schema-type", "object", schema["type"]!.GetValue<string>());
        var props = schema["properties"]!.AsObject();
        AssertEqual("prop-city-type", "string", props["city"]!.AsObject()["type"]!.GetValue<string>());
        AssertEqual("prop-enum-first", "c", props["unit"]!.AsObject()["enum"]!.AsArray()[0]!.GetValue<string>());
        AssertEqual("required-first", "city", schema["required"]!.AsArray()[0]!.GetValue<string>());
    }

    // ── assert helpers ──────────────────────────────────────────────────────────
    private static void AssertEqual<T>(string name, T expected, T actual)
    {
        if (Equals(expected, actual)) { Console.WriteLine($"  [PASS] {name}"); _passed++; }
        else { Console.Error.WriteLine($"  [FAIL] {name}: expected '{expected}', got '{actual}'"); _failed++; }
    }

    private static void AssertTrue(string name, bool cond)
    {
        if (cond) { Console.WriteLine($"  [PASS] {name}"); _passed++; }
        else { Console.Error.WriteLine($"  [FAIL] {name}: expected true"); _failed++; }
    }
}
