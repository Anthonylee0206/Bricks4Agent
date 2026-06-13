using Broker.Services;

namespace Unit.Tests.Services;

/// <summary>
/// HighLevelCommandParser — 純指令解析測試（無 DB、無 IO、無時間依賴）。
///
/// 涵蓋：
/// - 空字串 / 純空白 → Empty
/// - help 指令所有別名（半形 ?h / 全形 ？help / 大小寫）
/// - query 前綴（? / ？）+ 子指令別名（search/wiki/rail/hsr/bus/flight/profile）+ 參數切分
/// - production 前綴（/ / ／）+ name/id 別名 + 參數
/// - project-name 前綴（# / ＃）
/// - confirm / cancel token（中英文別名、大小寫）
/// - conversation fallback（未命中任何規則）
/// - 前綴優先序、前後空白 trim、全形前綴
/// - ParseProjectInterviewCommand 的 /proj /ok /revise /cancel
///
/// 全部走預設 HighLevelCoordinatorOptions（query 前綴 ? ／？，production 前綴 / ／／）。
/// </summary>
public class HighLevelCommandParserTests
{
    private static HighLevelCommandParser NewParser() => new(new HighLevelCoordinatorOptions());

    // ---------- Empty ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Parse_EmptyOrWhitespace_KindEmpty(string? raw)
    {
        var result = NewParser().Parse(raw);

        result.Kind.Should().Be(HighLevelInputKind.Empty);
        result.Trimmed.Should().BeEmpty();
        result.Normalized.Should().BeEmpty();
    }

    [Fact]
    public void Parse_Null_RawIsEmptyStringNotNull()
    {
        var result = NewParser().Parse(null);
        result.Raw.Should().Be(string.Empty);
    }

    // ---------- Help ----------

    [Theory]
    [InlineData("?help")]
    [InlineData("?Help")]
    [InlineData("?h")]
    [InlineData("?H")]
    [InlineData("？help")]
    [InlineData("？Help")]
    [InlineData("？h")]
    [InlineData("？H")]
    [InlineData("  ?help  ")]
    public void Parse_HelpAliases_KindHelp(string raw)
    {
        NewParser().Parse(raw).Kind.Should().Be(HighLevelInputKind.Help);
    }

    [Theory]
    [InlineData("?help")]
    [InlineData("？H")]
    [InlineData("  ?h ")]
    public void IsHelpCommand_RecognizesAliasesAndTrims(string message)
    {
        NewParser().IsHelpCommand(message).Should().BeTrue();
    }

    [Theory]
    [InlineData("help")]      // 沒前綴
    [InlineData("?helpme")]   // 不是精確 token
    [InlineData("?HELP")]     // 全大寫不在白名單
    [InlineData("?search x")] // 是 query 不是 help
    public void IsHelpCommand_RejectsNonExactTokens(string message)
    {
        NewParser().IsHelpCommand(message).Should().BeFalse();
    }

    // ---------- Query ----------

    [Theory]
    [InlineData("?search Taipei", "search", "Taipei")]
    [InlineData("?s Taipei", "search", "Taipei")]
    [InlineData("?搜尋 Taipei", "search", "Taipei")]   // 搜尋
    [InlineData("?wiki Newton", "wiki", "Newton")]
    [InlineData("?w Newton", "wiki", "Newton")]
    [InlineData("?維基 Newton", "wiki", "Newton")]     // 維基
    [InlineData("?rail TPE", "rail", "TPE")]
    [InlineData("?train TPE", "rail", "TPE")]
    [InlineData("?tra TPE", "rail", "TPE")]
    [InlineData("?火車 TPE", "rail", "TPE")]           // 火車
    [InlineData("?台鐵 TPE", "rail", "TPE")]           // 台鐵
    [InlineData("?hsr left", "hsr", "left")]
    [InlineData("?thsr left", "hsr", "left")]
    [InlineData("?高鐵 left", "hsr", "left")]          // 高鐵
    [InlineData("?bus 307", "bus", "307")]
    [InlineData("?b 307", "bus", "307")]
    [InlineData("?公車 307", "bus", "307")]            // 公車
    [InlineData("?客運 307", "bus", "307")]            // 客運
    [InlineData("?flight CI100", "flight", "CI100")]
    [InlineData("?f CI100", "flight", "CI100")]
    [InlineData("?flights CI100", "flight", "CI100")]
    [InlineData("?航班 CI100", "flight", "CI100")]     // 航班
    [InlineData("?機票 CI100", "flight", "CI100")]     // 機票
    [InlineData("?profile", "profile", "")]
    [InlineData("?me", "profile", "")]
    [InlineData("?whoami", "profile", "")]
    public void Parse_QueryCommands_ResolveCommandAndArgument(string raw, string expectedCommand, string expectedArg)
    {
        var result = NewParser().Parse(raw);

        result.Kind.Should().Be(HighLevelInputKind.Query);
        result.QueryCommand.Should().Be(expectedCommand);
        result.QueryArgument.Should().Be(expectedArg);
    }

    [Theory]
    [InlineData("?SEARCH Tokyo")]
    [InlineData("?Search Tokyo")]
    [InlineData("?SeArCh Tokyo")]
    public void Parse_QueryCommand_CommandTokenIsCaseInsensitive(string raw)
    {
        var result = NewParser().Parse(raw);
        result.QueryCommand.Should().Be("search");
        result.QueryArgument.Should().Be("Tokyo");
    }

    [Fact]
    public void Parse_QueryArgument_PreservesCaseAndOnlySplitsOnFirstWhitespace()
    {
        var result = NewParser().Parse("?search New York City");
        result.QueryCommand.Should().Be("search");
        // 只切第一個空白，參數整段（含原始大小寫）保留
        result.QueryArgument.Should().Be("New York City");
    }

    [Fact]
    public void Parse_QueryArgument_TrimmedAroundFirstSplit()
    {
        var result = NewParser().Parse("?search    spaced   ");
        result.QueryCommand.Should().Be("search");
        result.QueryArgument.Should().Be("spaced");
    }

    [Theory]
    [InlineData("?frobnicate stuff", "frobnicate stuff")]
    [InlineData("?unknown", "unknown")]
    public void Parse_QueryUnknownCommand_EmptyCommandWholeBodyAsArgument(string raw, string expectedArg)
    {
        var result = NewParser().Parse(raw);
        result.Kind.Should().Be(HighLevelInputKind.Query);
        result.QueryCommand.Should().BeEmpty();
        result.QueryArgument.Should().Be(expectedArg);
    }

    [Fact]
    public void Parse_QueryPrefixOnly_EmptyBody_NoCommandNoArgument()
    {
        var result = NewParser().Parse("?");
        result.Kind.Should().Be(HighLevelInputKind.Query);
        result.Body.Should().BeEmpty();
        result.QueryCommand.Should().BeEmpty();
        result.QueryArgument.Should().BeEmpty();
    }

    [Fact]
    public void Parse_FullwidthQueryPrefix_MatchedPrefixRecorded()
    {
        var result = NewParser().Parse("？bus 307");
        result.Kind.Should().Be(HighLevelInputKind.Query);
        result.Prefix.Should().Be("？");
        result.QueryCommand.Should().Be("bus");
        result.QueryArgument.Should().Be("307");
    }

    [Fact]
    public void Parse_QueryBody_NormalizedIsLowercasedBody()
    {
        var result = NewParser().Parse("?Rail TPE");
        result.Body.Should().Be("Rail TPE");
        result.Normalized.Should().Be("rail tpe");
    }

    // ---------- Production ----------

    [Theory]
    [InlineData("/name Alice", "name", "Alice")]
    [InlineData("/n Alice", "name", "Alice")]
    [InlineData("/display-name Alice", "name", "Alice")]
    [InlineData("/displayname Alice", "name", "Alice")]
    [InlineData("/稱呼 Alice", "name", "Alice")]     // 稱呼
    [InlineData("/id U123", "id", "U123")]
    [InlineData("/i U123", "id", "U123")]
    [InlineData("/user-id U123", "id", "U123")]
    [InlineData("/userid U123", "id", "U123")]
    [InlineData("/code U123", "id", "U123")]
    public void Parse_ProductionCommands_ResolveCommandAndArgument(string raw, string expectedCommand, string expectedArg)
    {
        var result = NewParser().Parse(raw);

        result.Kind.Should().Be(HighLevelInputKind.Production);
        result.ProductionCommand.Should().Be(expectedCommand);
        result.ProductionArgument.Should().Be(expectedArg);
    }

    [Theory]
    [InlineData("/NAME Bob")]
    [InlineData("/Name Bob")]
    public void Parse_ProductionCommand_CommandTokenIsCaseInsensitive(string raw)
    {
        var result = NewParser().Parse(raw);
        result.ProductionCommand.Should().Be("name");
        result.ProductionArgument.Should().Be("Bob");
    }

    [Fact]
    public void Parse_ProductionUnknownCommand_EmptyCommandWholeBodyAsArgument()
    {
        var result = NewParser().Parse("/wat something");
        result.Kind.Should().Be(HighLevelInputKind.Production);
        result.ProductionCommand.Should().BeEmpty();
        result.ProductionArgument.Should().Be("wat something");
    }

    [Fact]
    public void Parse_FullwidthProductionPrefix_MatchedPrefixRecorded()
    {
        var result = NewParser().Parse("／id U999");
        result.Kind.Should().Be(HighLevelInputKind.Production);
        result.Prefix.Should().Be("／");
        result.ProductionCommand.Should().Be("id");
        result.ProductionArgument.Should().Be("U999");
    }

    // ---------- Project name ----------

    [Theory]
    [InlineData("#MyProject", "#", "MyProject")]
    [InlineData("＃MyProject", "＃", "MyProject")]   // ＃ 全形
    [InlineData("  #Spaced Project  ", "#", "Spaced Project")]
    public void Parse_ProjectNamePrefix_KindProjectNameWithBody(string raw, string expectedPrefix, string expectedBody)
    {
        var result = NewParser().Parse(raw);

        result.Kind.Should().Be(HighLevelInputKind.ProjectName);
        result.Prefix.Should().Be(expectedPrefix);
        result.Body.Should().Be(expectedBody);
    }

    // ---------- Confirm / Cancel ----------

    [Theory]
    [InlineData("confirm")]
    [InlineData("CONFIRM")]
    [InlineData("yes")]
    [InlineData("Y")]
    [InlineData("ok")]
    [InlineData("OKAY")]
    [InlineData("確認")]   // 確認
    [InlineData("  ok  ")]
    public void Parse_ConfirmTokens_KindConfirm(string raw)
    {
        NewParser().Parse(raw).Kind.Should().Be(HighLevelInputKind.Confirm);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("CANCEL")]
    [InlineData("no")]
    [InlineData("N")]
    [InlineData("取消")]   // 取消
    public void Parse_CancelTokens_KindCancel(string raw)
    {
        NewParser().Parse(raw).Kind.Should().Be(HighLevelInputKind.Cancel);
    }

    // ---------- Conversation fallback ----------

    [Theory]
    [InlineData("hello there")]
    [InlineData("yesterday I went home")]   // 含 "yes" 但非單獨 token
    [InlineData("nope")]                    // 不是 cancel 的 "no"
    [InlineData("what is the weather")]
    public void Parse_PlainText_FallsBackToConversation(string raw)
    {
        var result = NewParser().Parse(raw);

        result.Kind.Should().Be(HighLevelInputKind.Conversation);
        result.Trimmed.Should().Be(raw.Trim());
        result.Body.Should().Be(raw.Trim());
        result.Normalized.Should().Be(raw.Trim().ToLowerInvariant());
    }

    // ---------- Precedence / ordering ----------

    [Fact]
    public void Parse_Precedence_HelpBeatsQueryPrefix()
    {
        // "?h" 同時是 query 前綴開頭，但 help 規則先判定
        NewParser().Parse("?h").Kind.Should().Be(HighLevelInputKind.Help);
    }

    [Fact]
    public void Parse_Precedence_QueryPrefixBeatsConfirmToken()
    {
        // "?yes" 走 query（前綴優先於 confirm token 判定）
        var result = NewParser().Parse("?yes");
        result.Kind.Should().Be(HighLevelInputKind.Query);
    }

    [Fact]
    public void Parse_LeadingTrailingWhitespace_TrimmedBeforeClassification()
    {
        var result = NewParser().Parse("   /name  Carol  ");
        result.Kind.Should().Be(HighLevelInputKind.Production);
        result.Trimmed.Should().Be("/name  Carol");
        result.ProductionCommand.Should().Be("name");
        result.ProductionArgument.Should().Be("Carol");
    }

    // ---------- ParseProjectInterviewCommand ----------

    [Theory]
    [InlineData("/proj", ProjectInterviewCommand.StartProjectInterview)]
    [InlineData("／proj", ProjectInterviewCommand.StartProjectInterview)]
    [InlineData("/ok", ProjectInterviewCommand.Approve)]
    [InlineData("／ok", ProjectInterviewCommand.Approve)]
    [InlineData("/revise", ProjectInterviewCommand.Revise)]
    [InlineData("／revise", ProjectInterviewCommand.Revise)]
    [InlineData("/cancel", ProjectInterviewCommand.Cancel)]
    [InlineData("／cancel", ProjectInterviewCommand.Cancel)]
    [InlineData("  /OK  ", ProjectInterviewCommand.Approve)]   // trim + lowercase 正規化
    public void ParseProjectInterviewCommand_RecognizedCommands(string raw, ProjectInterviewCommand expected)
    {
        var result = NewParser().ParseProjectInterviewCommand(raw);

        result.IsProjectInterview.Should().BeTrue();
        result.Command.Should().Be(expected);
    }

    [Theory]
    [InlineData("proj")]      // 沒前綴
    [InlineData("/project")]  // 不是精確 token
    [InlineData("hello")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseProjectInterviewCommand_Unrecognized_NotACommand(string? raw)
    {
        var result = NewParser().ParseProjectInterviewCommand(raw);

        result.IsProjectInterview.Should().BeFalse();
        result.Command.Should().BeNull();
    }
}
