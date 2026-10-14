using System.Text;
using System.Text.Json.Nodes;
using Broker.NyaChat;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Broker.Tests;

public static class NyaLlmTests
{
    private static int _passed;
    private static int _failed;

    public static (int passed, int failed) Run()
    {
        _passed = 0;
        _failed = 0;

        Console.WriteLine("=== NyaChat LLM Provider/Routing Tests (大項二) ===");
        Console.WriteLine();

        TestToolClosureSerialization();
        TestBuildToolsJsonEnum();
        TestApplyModelOverrides();
        TestRouterNamedProfile();
        TestRouterBaseUrlDecoupled();
        TestRouterTaskRoutingFallbackName();
        TestRouterLegacySynthesis();
        TestModelOverridesBindFromConfiguration();
        TestStripThinkTags();
        TestFacadeWarnsWhenToolsUnsupported();

        Console.WriteLine();
        Console.WriteLine($"=== NyaChat LLM Test Results: {_passed} passed, {_failed} failed ===");
        return (_passed, _failed);
    }

    private static void TestToolClosureSerialization()
    {
        Console.WriteLine("--- role=tool / assistant(tool_calls) serialization ---");

        var messages = new List<NyaLlmMessage>
        {
            new() { Role = "system", Content = "sys" },
            new()
            {
                Role = "assistant",
                Content = "",
                ToolCalls = new List<NyaToolCall>
                {
                    new() { Id = "call_1", FunctionName = "search", FunctionArguments = "{\"query\":\"x\"}" }
                }
            },
            new() { Role = "tool", Content = "搜尋結果", ToolCallId = "call_1", Name = "search" }
        };

        var arr = NyaLlmSerialization.BuildJsonMessages(messages);

        var assistant = arr[1]!.AsObject();
        AssertEqual("assistant-role", "assistant", assistant["role"]!.GetValue<string>());
        var fn = assistant["tool_calls"]!.AsArray()[0]!.AsObject()["function"]!.AsObject();
        AssertEqual("toolcall-name", "search", fn["name"]!.GetValue<string>());
        AssertEqual("toolcall-args", "{\"query\":\"x\"}", fn["arguments"]!.GetValue<string>());

        var tool = arr[2]!.AsObject();
        AssertEqual("tool-role", "tool", tool["role"]!.GetValue<string>());
        AssertEqual("tool-call-id", "call_1", tool["tool_call_id"]!.GetValue<string>());
        AssertEqual("tool-name", "search", tool["name"]!.GetValue<string>());
        AssertEqual("tool-content", "搜尋結果", tool["content"]!.GetValue<string>());
    }

    private static void TestBuildToolsJsonEnum()
    {
        Console.WriteLine("--- BuildToolsJson enum ---");
        var tools = new List<NyaLlmTool>
        {
            new()
            {
                Function = new NyaLlmFunction
                {
                    Name = "place_order",
                    Description = "下單",
                    Parameters = new NyaLlmFunctionParameters
                    {
                        Properties = new Dictionary<string, NyaLlmParameterProperty>
                        {
                            ["action"] = new() { Type = "string", Description = "買或賣", Enum = "[\"buy\",\"sell\"]" }
                        },
                        Required = new List<string> { "action" }
                    }
                }
            }
        };

        var arr = NyaLlmSerialization.BuildToolsJson(tools);
        var props = arr[0]!.AsObject()["function"]!.AsObject()["parameters"]!.AsObject()["properties"]!.AsObject();
        var enumArr = props["action"]!.AsObject()["enum"]!.AsArray();
        AssertEqual("enum-count", 2, enumArr.Count);
        AssertEqual("enum-first", "buy", enumArr[0]!.GetValue<string>());
        var required = arr[0]!.AsObject()["function"]!.AsObject()["parameters"]!.AsObject()["required"]!.AsArray();
        AssertEqual("required-first", "action", required[0]!.GetValue<string>());
    }

    private static void TestApplyModelOverrides()
    {
        Console.WriteLine("--- ApplyModelOverrides (2C) ---");

        var profile = new NyaLlmProfile
        {
            Model = "qwen3.6:35b-a3b-Q4",
            ModelOverrides = new Dictionary<string, double> { ["min_p"] = 0, ["presence_penalty"] = 1.5, ["top_k"] = 20 }
        };

        var target = new JsonObject { ["temperature"] = 1.0 };
        NyaLlmSerialization.ApplyModelOverrides(target, profile);
        AssertEqual("override-min_p", 0d, target["min_p"]!.GetValue<double>());
        AssertEqual("override-presence", 1.5d, target["presence_penalty"]!.GetValue<double>());
        AssertEqual("override-top_k", 20d, target["top_k"]!.GetValue<double>());

        var none = new JsonObject();
        NyaLlmSerialization.ApplyModelOverrides(none, new NyaLlmProfile { Model = "m" });
        AssertTrue("override-null-empty", none.Count == 0);
    }

    private static void TestRouterNamedProfile()
    {
        Console.WriteLine("--- StaticTaskRouter named profile ---");
        var router = new StaticTaskRouter(NullLogger<StaticTaskRouter>.Instance);
        var cfg = new NyaChatConfig
        {
            LlmProfiles = new()
            {
                ["chat_default"] = new NyaLlmProfile { Provider = "openai_chat", BaseUrl = "http://chat", Model = "big" }
            },
            TaskRouting = new() { ["chat"] = "chat_default" }
        };

        var chat = router.Resolve(NyaLlmTasks.Chat, cfg);
        AssertEqual("named-provider", "openai_chat", chat.Provider);
        AssertEqual("named-model", "big", chat.Model);
        AssertEqual("named-baseurl", "http://chat", chat.BaseUrl);
    }

    private static void TestRouterBaseUrlDecoupled()
    {
        Console.WriteLine("--- StaticTaskRouter BaseUrl decoupled (審查1) ---");
        var router = new StaticTaskRouter(NullLogger<StaticTaskRouter>.Instance);
        var cfg = new NyaChatConfig
        {
            LlmProfiles = new()
            {
                ["chat_default"] = new NyaLlmProfile { Provider = "openai_chat", BaseUrl = "http://openai", Model = "big" },
                ["extraction"]   = new NyaLlmProfile { Provider = "ollama", BaseUrl = "http://ollama", Model = "small" }
            },
            TaskRouting = new() { ["chat"] = "chat_default", ["summarization"] = "extraction" }
        };

        var chat = router.Resolve(NyaLlmTasks.Chat, cfg);
        var ext  = router.Resolve(NyaLlmTasks.Summarization, cfg);

        AssertEqual("decoupled-chat-url", "http://openai", chat.BaseUrl);
        AssertEqual("decoupled-ext-url", "http://ollama", ext.BaseUrl);
        AssertEqual("decoupled-ext-provider", "ollama", ext.Provider);
        AssertTrue("decoupled-urls-differ", chat.BaseUrl != ext.BaseUrl);
    }

    private static void TestRouterTaskRoutingFallbackName()
    {
        Console.WriteLine("--- StaticTaskRouter default profile name fallback ---");
        var router = new StaticTaskRouter(NullLogger<StaticTaskRouter>.Instance);
        var cfg = new NyaChatConfig
        {

            LlmProfiles = new() { ["summarization"] = new NyaLlmProfile { Provider = "ollama", Model = "sum-model" } },
            TaskRouting = new()
        };

        var sum = router.Resolve(NyaLlmTasks.Summarization, cfg);
        AssertEqual("fallback-name-model", "sum-model", sum.Model);
    }

    private static void TestRouterLegacySynthesis()
    {
        Console.WriteLine("--- StaticTaskRouter legacy synthesis ---");
        var router = new StaticTaskRouter(NullLogger<StaticTaskRouter>.Instance);

        var cfg = new NyaChatConfig
        {
            ChatProvider = "openai_chat",
            ChatBaseUrl = "http://legacy-chat",
            ChatModel = "cm",
            Temperature = 1.0f,
            TopP = 0.95f,
            SummarizationProvider = "ollama",
            SummarizationModel = ""
        };

        var chat = router.Resolve(NyaLlmTasks.Chat, cfg);
        AssertEqual("legacy-chat-provider", "openai_chat", chat.Provider);
        AssertEqual("legacy-chat-model", "cm", chat.Model);
        AssertEqual("legacy-chat-temp", 1.0f, chat.Temperature);

        var ext = router.Resolve(NyaLlmTasks.Summarization, cfg);
        AssertEqual("legacy-ext-provider", "ollama", ext.Provider);
        AssertEqual("legacy-ext-model-inherited", "cm", ext.Model);
        AssertEqual("legacy-ext-temp-low", 0.3f, ext.Temperature);
        AssertEqual("legacy-ext-baseurl", "http://legacy-chat", ext.BaseUrl);

        var sum = router.Resolve(NyaLlmTasks.Summarization, cfg);
        AssertEqual("legacy-sum-temp-low", 0.3f, sum.Temperature);
    }

    private static void TestModelOverridesBindFromConfiguration()
    {
        Console.WriteLine("--- ModelOverrides binds via ConfigurationBinder (appsettings path) ---");
        const string json = """
        {
          "NyaChat": {
            "LlmProfiles": {
              "chat_default": {
                "Provider": "openai_chat",
                "Model": "qwen3.6:35b-a3b-Q4",
                "ModelOverrides": { "min_p": 0, "presence_penalty": 1.5, "top_k": 20 }
              }
            },
            "TaskRouting": { "chat": "chat_default" }
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        var nya = config.GetSection("NyaChat").Get<NyaChatConfig>();

        AssertTrue("bind-not-null", nya != null);
        AssertEqual("bind-profile-model", "qwen3.6:35b-a3b-Q4", nya!.LlmProfiles["chat_default"].Model);
        var overrides = nya.LlmProfiles["chat_default"].ModelOverrides!;
        AssertEqual("bind-min_p", 0d, overrides["min_p"]);
        AssertEqual("bind-presence", 1.5d, overrides["presence_penalty"]);
        AssertEqual("bind-top_k", 20d, overrides["top_k"]);
        AssertEqual("bind-routing", "chat_default", nya.TaskRouting["chat"]);
    }

    private static void TestStripThinkTags()
    {
        Console.WriteLine("--- StripThinkTags ---");
        AssertEqual("strip-think", "answer",
            NyaLlmSerialization.StripThinkTags("<think>reasoning</think>answer"));
        AssertEqual("strip-plain", "hello", NyaLlmSerialization.StripThinkTags("  hello  "));
    }

    private static void TestFacadeWarnsWhenToolsUnsupported()
    {

        Console.WriteLine("--- ctor fails fast when chat resolves to no-tools provider (QC P5 升級為 fail-fast) ---");

        var threw = false;
        try { WithFacade(supportsTools: false, (_, _) => { }); }
        catch (InvalidOperationException ex)
        {
            threw = true;
            AssertTrue("ctor-msg-mentions-tools", ex.Message.Contains("does not support tools"));
        }
        AssertTrue("ctor-fails-fast-when-unsupported", threw);

        WithFacade(supportsTools: true, (clientYes, logYes) =>
        {
            var withTools = new List<NyaLlmTool> { new() { Function = new NyaLlmFunction { Name = "search" } } };
            clientYes.ChatAsync(new NyaLlmRequest { Tools = withTools }).GetAwaiter().GetResult();
            AssertTrue("no-warn-when-supported",
                !logYes.Warnings.Any(w => w.Contains("does not support tools")));
        });
    }

    private static void WithFacade(bool supportsTools, Action<NyaLlmClient, CapturingLogger<NyaLlmClient>> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nya_llmfacade_{Guid.NewGuid():N}.db");
        var db = BrokerCore.Data.BrokerDb.UseSqlite($"Data Source={path};Pooling=False");
        try
        {
            var store = new NyaLlmProfileStore(db, NullLogger<NyaLlmProfileStore>.Instance);
            store.UpsertProfile(NyaLlmProfileEntry.FromProfile("chatp", "x",
                new NyaLlmProfile { Provider = "p", Model = "m" }));
            store.SetRoute(NyaLlmProfileStore.DefaultCallSite, "chatp");
            store.SetRoute("chat", "chatp");
            var log = new CapturingLogger<NyaLlmClient>();
            var client = new NyaLlmClient(
                store,
                new INyaLlmProvider[] { new FakeProvider { Key = "p", SupportsTools = supportsTools } },
                log);
            body(client, log);
        }
        finally
        {
            db.Dispose();
            foreach (var p in new[] { path, path + "-shm", path + "-wal" })
                try { if (File.Exists(p)) File.Delete(p); } catch {  }
        }
    }

    private sealed class FakeProvider : INyaLlmProvider
    {
        public string Key { get; init; } = "fake";
        public bool SupportsTools { get; init; } = true;
        public Task<NyaLlmResponse?> SendAsync(NyaLlmProviderRequest request, CancellationToken ct)
            => Task.FromResult<NyaLlmResponse?>(new NyaLlmResponse { Content = "ok" });
    }

    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public readonly List<string> Warnings = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

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
