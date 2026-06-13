using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Broker.Services;

namespace Unit.Tests.Services;

/// <summary>
/// HighLevelExecutionModelPlanner.RecommendAsync — 確定性執行模型路由 / 計畫邏輯測試。
///
/// 此 planner 的「決策」是純邏輯：給定 policy（Enabled + catalog）與一段固定的 LLM 回覆，
/// 輸出哪個 catalog entry 被選中、回傳的 Model/Tier 從何而來、reason fallback、以及各 guard 分支。
/// LLM 本身是 HTTP 呼叫，但透過 FakeHttpMessageHandler 餵入固定回覆即可把「呼叫後的解析/比對/計畫」
/// 變成確定性（與既有 TdxTransportProviderTests 同一手法）。因此這裡測的是 deterministic routing/plan，
/// 而非 LLM 的非確定性內容。
///
/// 涵蓋：
/// - !Enabled → null（不觸網）
/// - catalog 全空 / 全被過濾（disabled / 空 alias / 空 model）→ null
/// - catalog 過濾：只保留 Enabled 且 alias/model 皆非空者
/// - provider 路由：ollama→api/chat、openai chat→v1/chat/completions、responses→v1/responses
/// - LLM 回覆解析：缺 alias / 空白 alias / 未知 alias / 非 JSON → null
/// - 命中 alias（含大小寫不敏感）→ Model/Tier 取自 matched entry（非 LLM 回覆）
/// - reason：LLM 給了用 LLM 的、空白則用 task_type fallback
/// - 固定欄位：RequestedBy / ValidationStatus
/// </summary>
public class HighLevelExecutionModelPlannerTests
{
    // ---------- helpers ----------

    private sealed class CannedHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;
        public string? LastRelativePath { get; private set; }
        public int CallCount { get; private set; }

        public CannedHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            // 紀錄被呼叫的相對路徑，以驗證 provider 路由分支。
            LastRelativePath = request.RequestUri!.AbsolutePath.TrimStart('/');
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    private static HighLevelExecutionModelPlanner NewPlanner(
        HighLevelExecutionModelPolicyOptions policy,
        out CannedHandler handler,
        string ollamaReply = "",
        string openAiChatReply = "",
        string responsesReply = "",
        string provider = "ollama",
        string apiFormat = "chat",
        HttpStatusCode status = HttpStatusCode.OK)
    {
        // 依 provider 包出對應 API 的回覆外殼，讓 CallLlmAsync 解析出 ollamaReply/...。
        string envelope = provider.ToLowerInvariant() switch
        {
            "ollama" => "{\"message\":{\"content\":" + ToJsonString(ollamaReply) + "}}",
            _ when string.Equals(apiFormat, "responses", StringComparison.OrdinalIgnoreCase)
                => "{\"output_text\":" + ToJsonString(responsesReply) + "}",
            _ => "{\"choices\":[{\"message\":{\"content\":" + ToJsonString(openAiChatReply) + "}}]}"
        };

        handler = new CannedHandler(envelope, status);
        var capturedHandler = handler;

        var factory = NSubstitute.Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>())
            .Returns(_ => new HttpClient(capturedHandler) { BaseAddress = new Uri("http://llm.test/") });

        var llmOptions = new HighLevelLlmOptions
        {
            Provider = provider,
            ApiFormat = apiFormat,
            DefaultModel = "test-model"
        };

        return new HighLevelExecutionModelPlanner(
            policy,
            llmOptions,
            factory,
            NullLogger<HighLevelExecutionModelPlanner>.Instance);
    }

    private static string ToJsonString(string raw) =>
        System.Text.Json.JsonSerializer.Serialize(raw);

    private static HighLevelExecutionModelPolicyOptions PolicyWith(params HighLevelExecutionModelCatalogEntry[] entries) =>
        new() { Enabled = true, Catalog = entries.ToList() };

    private static HighLevelExecutionModelCatalogEntry Entry(
        string alias, string model = "m", string tier = "t", string description = "d", bool enabled = true) =>
        new() { Alias = alias, Model = model, Tier = tier, Description = description, Enabled = enabled };

    private static HighLevelTaskDraft Draft(string taskType = "analysis") =>
        new() { TaskType = taskType, Title = "T", Summary = "S", OriginalMessage = "msg" };

    private static HighLevelMemoryState Memory(string? goal = null) =>
        new() { CurrentGoal = goal };

    // ---------- Guard: disabled policy ----------

    [Fact]
    public async Task RecommendAsync_PolicyDisabled_ReturnsNull_WithoutCallingLlm()
    {
        var policy = PolicyWith(Entry("fast"));
        policy.Enabled = false;
        var planner = NewPlanner(policy, out var handler, ollamaReply: """{"alias":"fast"}""");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().BeNull();
        handler.CallCount.Should().Be(0, "disabled policy 必須在觸網前短路");
    }

    // ---------- Guard: empty / fully-filtered catalog ----------

    [Fact]
    public async Task RecommendAsync_EmptyCatalog_ReturnsNull_WithoutCallingLlm()
    {
        var planner = NewPlanner(PolicyWith(), out var handler, ollamaReply: """{"alias":"x"}""");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().BeNull();
        handler.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData(false, "alias", "model")]  // disabled
    [InlineData(true, "", "model")]        // blank alias
    [InlineData(true, "   ", "model")]     // whitespace alias
    [InlineData(true, "alias", "")]        // blank model
    [InlineData(true, "alias", "   ")]     // whitespace model
    public async Task RecommendAsync_AllCatalogEntriesFilteredOut_ReturnsNull(bool enabled, string alias, string model)
    {
        var policy = PolicyWith(Entry(alias, model: model, enabled: enabled));
        var planner = NewPlanner(policy, out var handler, ollamaReply: $$"""{"alias":{{ToJsonString(alias)}}}""");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().BeNull("唯一 catalog entry 不合格時等同空 catalog");
        handler.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task RecommendAsync_DisabledEntryIsExcluded_OnlyEnabledMatchable()
    {
        // disabled "fast" + enabled "smart"；LLM 回 disabled 的 alias → 視為 unknown → null
        var policy = PolicyWith(
            Entry("fast", enabled: false),
            Entry("smart", model: "smart-model", tier: "premium"));
        var planner = NewPlanner(policy, out _, ollamaReply: """{"alias":"fast"}""");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().BeNull("disabled 的 fast 已被過濾，不在可選清單");
    }

    // ---------- Provider routing ----------

    [Fact]
    public async Task RecommendAsync_OllamaProvider_HitsApiChatEndpoint()
    {
        var planner = NewPlanner(PolicyWith(Entry("a")), out var handler,
            ollamaReply: """{"alias":"a"}""", provider: "ollama");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().NotBeNull();
        handler.LastRelativePath.Should().Be("api/chat");
    }

    [Fact]
    public async Task RecommendAsync_OpenAiChatProvider_HitsChatCompletionsEndpoint()
    {
        var planner = NewPlanner(PolicyWith(Entry("a")), out var handler,
            openAiChatReply: """{"alias":"a"}""", provider: "openai", apiFormat: "chat");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().NotBeNull();
        handler.LastRelativePath.Should().Be("v1/chat/completions");
    }

    [Fact]
    public async Task RecommendAsync_ResponsesApiFormat_HitsResponsesEndpoint()
    {
        var planner = NewPlanner(PolicyWith(Entry("a")), out var handler,
            responsesReply: """{"alias":"a"}""", provider: "openai", apiFormat: "responses");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().NotBeNull();
        handler.LastRelativePath.Should().Be("v1/responses");
    }

    // ---------- LLM reply parsing failures ----------

    [Fact]
    public async Task RecommendAsync_EmptyLlmReply_ReturnsNull()
    {
        var planner = NewPlanner(PolicyWith(Entry("a")), out _, ollamaReply: "   ");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().BeNull();
    }

    [Fact]
    public async Task RecommendAsync_ReplyMissingAliasProperty_ReturnsNull()
    {
        var planner = NewPlanner(PolicyWith(Entry("a")), out _, ollamaReply: """{"reason":"no alias here"}""");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    public async Task RecommendAsync_BlankOrNullAlias_ReturnsNull(string aliasJson)
    {
        var planner = NewPlanner(PolicyWith(Entry("a")), out _, ollamaReply: $$"""{"alias":{{aliasJson}}}""");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().BeNull();
    }

    [Fact]
    public async Task RecommendAsync_UnknownAlias_ReturnsNull()
    {
        var planner = NewPlanner(PolicyWith(Entry("fast"), Entry("smart")), out _,
            ollamaReply: """{"alias":"does-not-exist"}""");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().BeNull("LLM 不能創造 catalog 以外的 alias");
    }

    [Fact]
    public async Task RecommendAsync_NonJsonReply_ReturnsNull()
    {
        var planner = NewPlanner(PolicyWith(Entry("a")), out _, ollamaReply: "this is not json at all");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().BeNull("JsonException 被吞並回 null");
    }

    [Fact]
    public async Task RecommendAsync_LlmHttpError_ReturnsNull()
    {
        var planner = NewPlanner(PolicyWith(Entry("a")), out _,
            ollamaReply: """{"alias":"a"}""", status: HttpStatusCode.InternalServerError);

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().BeNull("非 2xx → CallLlm 回 null → planner 回 null");
    }

    // ---------- Successful match: Model/Tier come from catalog ----------

    [Fact]
    public async Task RecommendAsync_MatchedAlias_ReturnsCatalogModelAndTier_NotLlmEcho()
    {
        // 即使 LLM 在 reply 內塞了不同的 model/tier，輸出仍取自 matched catalog entry。
        var policy = PolicyWith(
            Entry("fast", model: "fast-1", tier: "cheap"),
            Entry("smart", model: "smart-9", tier: "premium"));
        var planner = NewPlanner(policy, out _,
            ollamaReply: """{"alias":"smart","model":"HALLUCINATED","tier":"HALLUCINATED","reason":"needs深度推理"}""");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().NotBeNull();
        result!.Alias.Should().Be("smart");
        result.Model.Should().Be("smart-9", "Model 必須來自 catalog，不能信任 LLM 回的值");
        result.Tier.Should().Be("premium");
        result.Reason.Should().Be("needs深度推理");
        result.RequestedBy.Should().Be("high-level-entry-model");
        result.ValidationStatus.Should().Be("validated");
    }

    [Theory]
    [InlineData("SMART")]
    [InlineData("smart")]
    [InlineData("SmArT")]
    public async Task RecommendAsync_AliasMatchIsCaseInsensitive_NormalizesToCatalogCasing(string replyAlias)
    {
        var policy = PolicyWith(Entry("smart", model: "smart-9", tier: "premium"));
        var planner = NewPlanner(policy, out _, ollamaReply: $$"""{"alias":{{ToJsonString(replyAlias)}}}""");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().NotBeNull();
        result!.Alias.Should().Be("smart", "輸出採用 catalog 內的 alias 大小寫");
    }

    [Fact]
    public async Task RecommendAsync_FirstMatchWinsWhenDuplicateAliases()
    {
        // 兩個同 alias entry：FirstOrDefault → 取第一個的 model/tier。
        var policy = PolicyWith(
            Entry("dup", model: "first-model", tier: "first-tier"),
            Entry("dup", model: "second-model", tier: "second-tier"));
        var planner = NewPlanner(policy, out _, ollamaReply: """{"alias":"dup"}""");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().NotBeNull();
        result!.Model.Should().Be("first-model");
        result.Tier.Should().Be("first-tier");
    }

    // ---------- Reason fallback ----------

    [Theory]
    [InlineData("""{"alias":"a"}""")]                  // 完全沒 reason 欄位
    [InlineData("""{"alias":"a","reason":""}""")]      // 空字串 reason
    [InlineData("""{"alias":"a","reason":"   "}""")]   // 空白 reason
    [InlineData("""{"alias":"a","reason":null}""")]    // null reason
    public async Task RecommendAsync_BlankOrMissingReason_FallsBackToTaskTypeReason(string reply)
    {
        var policy = PolicyWith(Entry("a"));
        var planner = NewPlanner(policy, out _, ollamaReply: reply);

        var result = await planner.RecommendAsync(Draft(taskType: "code_gen"), Memory());

        result.Should().NotBeNull();
        result!.Reason.Should().Be("requested by high-level entry model for task_type=code_gen");
    }

    [Fact]
    public async Task RecommendAsync_NonBlankReason_IsTrimmedAndPreserved()
    {
        var planner = NewPlanner(PolicyWith(Entry("a")), out _,
            ollamaReply: """{"alias":"a","reason":"  deliberate choice  "}""");

        var result = await planner.RecommendAsync(Draft(), Memory());

        result.Should().NotBeNull();
        result!.Reason.Should().Be("deliberate choice");
    }
}
