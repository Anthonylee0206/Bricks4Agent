using Broker.Services;

namespace Unit.Tests.Services;

/// <summary>
/// HighLevelInputTrustPolicy — 輸入信任/汙染(taint)治理政策測試。
///
/// 安全核心:只有「未經轉換、source=UserMessage、taint ∈ {UserText,TrustedControl}」
/// 的原始使用者輸入才可被升格成可執行命令(Production/Confirm/Cancel/...)。
/// 任何外部來源(ToolOutput/RetrievedDocument/DecodedPayload/SystemState)、
/// 任何 taint 升級(ExternalText/TransformedText)、或任何 transform 痕跡,
/// 都必須拒絕命令升格、降級為純對話。
///
/// 只測 Evaluate()(純判定)與 Apply()(降級轉換)的確定性邏輯。
/// 不測 DB / parser pipeline。
/// </summary>
public class HighLevelInputTrustPolicyTests
{
    private static HighLevelInputTrustPolicy Policy() => new();

    private static HighLevelParsedInput Command(
        HighLevelInputKind kind = HighLevelInputKind.Production,
        string raw = "/prod ship",
        string prefix = "/prod",
        string body = "ship")
        => new()
        {
            Kind = kind,
            Raw = raw,
            Trimmed = raw.Trim(),
            Prefix = prefix,
            Body = body,
            Normalized = raw.Trim().ToLowerInvariant()
        };

    private static HighLevelInputEnvelope Envelope(
        HighLevelInputSource source = HighLevelInputSource.UserMessage,
        HighLevelInputTaint taint = HighLevelInputTaint.UserText,
        params HighLevelTransformKind[] transforms)
        => new()
        {
            RawText = "/prod ship",
            Source = source,
            Taint = taint,
            Transforms = transforms.ToList()
        };

    // ---- Evaluate: non-command content is always allowed regardless of provenance ----

    [Theory]
    [InlineData(HighLevelInputKind.Empty)]
    [InlineData(HighLevelInputKind.Conversation)]
    public void Evaluate_NonCommandKind_AlwaysAllowed_EvenFromUntrustedSource(HighLevelInputKind kind)
    {
        // Worst-case provenance: external retrieved doc, transformed, hostile taint.
        var env = Envelope(
            HighLevelInputSource.RetrievedDocument,
            HighLevelInputTaint.TransformedText,
            HighLevelTransformKind.Base64Decode);
        var parsed = Command(kind);

        var decision = Policy().Evaluate(env, parsed);

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().Contain("non-command");
    }

    // ---- Evaluate: trusted command paths ----

    [Theory]
    [InlineData(HighLevelInputKind.Production)]
    [InlineData(HighLevelInputKind.Confirm)]
    [InlineData(HighLevelInputKind.Cancel)]
    [InlineData(HighLevelInputKind.Query)]
    [InlineData(HighLevelInputKind.Help)]
    [InlineData(HighLevelInputKind.ProjectName)]
    public void Evaluate_RawUserMessage_UserText_NoTransforms_AllowsCommand(HighLevelInputKind kind)
    {
        var decision = Policy().Evaluate(
            Envelope(HighLevelInputSource.UserMessage, HighLevelInputTaint.UserText),
            Command(kind));

        decision.Allowed.Should().BeTrue();
        decision.Reason.Should().Contain("raw user input");
    }

    [Fact]
    public void Evaluate_TrustedControlTaint_AllowsCommand()
    {
        var decision = Policy().Evaluate(
            Envelope(HighLevelInputSource.UserMessage, HighLevelInputTaint.TrustedControl),
            Command());

        decision.Allowed.Should().BeTrue();
    }

    // ---- Evaluate: source gate ----

    [Theory]
    [InlineData(HighLevelInputSource.ToolOutput)]
    [InlineData(HighLevelInputSource.RetrievedDocument)]
    [InlineData(HighLevelInputSource.DecodedPayload)]
    [InlineData(HighLevelInputSource.SystemState)]
    public void Evaluate_NonUserMessageSource_DeniesCommand(HighLevelInputSource source)
    {
        // Even with the most trusting taint and no transforms, a non-user source
        // must never be promoted into a command (prompt-injection guard).
        var decision = Policy().Evaluate(
            Envelope(source, HighLevelInputTaint.TrustedControl),
            Command());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("only raw user messages");
    }

    // ---- Evaluate: taint gate ----

    [Theory]
    [InlineData(HighLevelInputTaint.ExternalText)]
    [InlineData(HighLevelInputTaint.TransformedText)]
    public void Evaluate_DisallowedTaint_DeniesCommand(HighLevelInputTaint taint)
    {
        var decision = Policy().Evaluate(
            Envelope(HighLevelInputSource.UserMessage, taint),
            Command());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("taint level");
    }

    // ---- Evaluate: transform gate ----

    [Fact]
    public void Evaluate_AnyTransform_DeniesCommand()
    {
        var decision = Policy().Evaluate(
            Envelope(HighLevelInputSource.UserMessage, HighLevelInputTaint.UserText,
                HighLevelTransformKind.Base64Decode),
            Command());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("transformed content");
    }

    [Fact]
    public void Evaluate_MultipleTransforms_DeniesCommand()
    {
        var decision = Policy().Evaluate(
            Envelope(HighLevelInputSource.UserMessage, HighLevelInputTaint.UserText,
                HighLevelTransformKind.UrlDecode, HighLevelTransformKind.HtmlDecode),
            Command());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("transformed content");
    }

    // ---- Evaluate: gate precedence / ordering ----

    [Fact]
    public void Evaluate_SourceCheckedBeforeTaint()
    {
        // Bad source AND bad taint → source reason wins (source gate runs first).
        var decision = Policy().Evaluate(
            Envelope(HighLevelInputSource.ToolOutput, HighLevelInputTaint.ExternalText),
            Command());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("only raw user messages");
    }

    [Fact]
    public void Evaluate_TaintCheckedBeforeTransforms()
    {
        // Bad taint AND a transform present → taint reason wins (taint gate runs first).
        var decision = Policy().Evaluate(
            Envelope(HighLevelInputSource.UserMessage, HighLevelInputTaint.ExternalText,
                HighLevelTransformKind.MarkdownStrip),
            Command());

        decision.Allowed.Should().BeFalse();
        decision.Reason.Should().Contain("taint level");
    }

    // ---- Apply: allowed path passes parsed input through unchanged ----

    [Fact]
    public void Apply_Allowed_ReturnsOriginalParsed_Unmodified()
    {
        var parsed = Command();
        var result = Policy().Apply(
            Envelope(HighLevelInputSource.UserMessage, HighLevelInputTaint.UserText),
            parsed);

        result.Trust.Allowed.Should().BeTrue();
        result.Parsed.Should().BeSameAs(parsed);
        result.Parsed.Kind.Should().Be(HighLevelInputKind.Production);
        result.Parsed.Prefix.Should().Be("/prod");
        result.Parsed.Body.Should().Be("ship");
    }

    // ---- Apply: denied path downgrades command → conversation, strips command framing ----

    [Fact]
    public void Apply_DeniedCommand_DowngradesToConversation_AndStripsPrefix()
    {
        var parsed = Command(HighLevelInputKind.Production, raw: "  /prod ship now  ", prefix: "/prod", body: "ship now");
        var result = Policy().Apply(
            // Untrusted source forces denial.
            Envelope(HighLevelInputSource.RetrievedDocument, HighLevelInputTaint.UserText),
            parsed);

        result.Trust.Allowed.Should().BeFalse();
        // Command kind neutralized so it can't execute.
        result.Parsed.Kind.Should().Be(HighLevelInputKind.Conversation);
        // Command framing stripped; body collapses to the trimmed text.
        result.Parsed.Prefix.Should().BeEmpty();
        result.Parsed.Body.Should().Be(parsed.Trimmed);
        // Raw provenance fields preserved.
        result.Parsed.Raw.Should().Be("  /prod ship now  ");
        result.Parsed.Trimmed.Should().Be("/prod ship now");
        result.Parsed.Normalized.Should().Be(parsed.Normalized);
    }

    [Fact]
    public void Apply_DeniedEmptyKind_StaysEmpty_NotConversation()
    {
        var parsed = Command(HighLevelInputKind.Empty, raw: "", prefix: "", body: "");
        var result = Policy().Apply(
            Envelope(HighLevelInputSource.ToolOutput, HighLevelInputTaint.UserText),
            parsed);

        // Empty is non-command so Evaluate actually allows it; assert the
        // allowed-path passthrough rather than a spurious downgrade.
        result.Trust.Allowed.Should().BeTrue();
        result.Parsed.Should().BeSameAs(parsed);
        result.Parsed.Kind.Should().Be(HighLevelInputKind.Empty);
    }

    [Fact]
    public void Apply_DeniedTransformedCommand_Downgrades()
    {
        var parsed = Command(HighLevelInputKind.Confirm, raw: "/yes", prefix: "/yes", body: "");
        var result = Policy().Apply(
            Envelope(HighLevelInputSource.UserMessage, HighLevelInputTaint.UserText,
                HighLevelTransformKind.Base64Decode),
            parsed);

        result.Trust.Allowed.Should().BeFalse();
        result.Parsed.Kind.Should().Be(HighLevelInputKind.Conversation);
        result.Parsed.Prefix.Should().BeEmpty();
        result.Parsed.Body.Should().Be(parsed.Trimmed);
    }
}
