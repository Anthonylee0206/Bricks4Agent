using FunctionPool.ContainerLogs;

namespace Unit.Tests.FunctionPool;

/// <summary>
/// 容器日誌錯誤分類(ErrorCatalog)—— 純 regex 邏輯、原本 0% 覆蓋。給即時日誌掃描 + 儀表板用。
/// 驗 severity 偵測(含 false-positive 排除)、ANSI 清理、分類 fallback。
/// </summary>
public class ErrorCatalogTests
{
    private static readonly string Esc = ((char)27).ToString();  // ANSI ESC（避免原始碼裡放隱形控制字元）

    [Theory]
    [InlineData("INFO: service started", "INFO")]
    [InlineData("ERROR: connection failed", "ERROR")]
    [InlineData("Unhandled exception in handler", "ERROR")]
    [InlineData("request timed out after 30s", "ERROR")]
    [InlineData("WARNING: deprecated config key", "WARN")]
    [InlineData("Build succeeded, 0 errors", "INFO")]   // false-positive:含 "errors" 但是成功訊息
    [InlineData("completed successfully", "INFO")]       // false-positive:successfully
    [InlineData("", "INFO")]
    public void DetectSeverity_ClassifiesLine(string line, string expected)
        => ErrorCatalog.DetectSeverity(line).Should().Be(expected);

    [Fact]
    public void StripAnsi_RemovesColorAndCursorCodes()
    {
        ErrorCatalog.StripAnsi($"{Esc}[31mRED{Esc}[0m").Should().Be("RED");        // 色碼
        ErrorCatalog.StripAnsi($"{Esc}[2J{Esc}[Hcleared").Should().Be("cleared");  // 清屏 + 游標歸位
    }

    [Fact]
    public void StripAnsi_PlainText_Unchanged()
        => ErrorCatalog.StripAnsi("plain log line").Should().Be("plain log line");

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void StripAnsi_EmptyOrNull_ReturnsEmpty(string? text)
        => ErrorCatalog.StripAnsi(text!).Should().BeEmpty();

    [Fact]
    public void Classify_EmptyMessage_FallsBackByLevelHint()
    {
        ErrorCatalog.Classify("", "WARN").Code.Should().Be("WRN-999");
        ErrorCatalog.Classify("", "ERROR").Code.Should().Be("ERR-999");
    }

    [Fact]
    public void Classify_KnownErrorMessage_ReturnsErrorEntry()
    {
        // 命中錯誤關鍵字、又非空訊息 → 必為 ERROR 等級的某個 catalog entry(已知或 ERR-999 fallback)
        var entry = ErrorCatalog.Classify("Unhandled exception: object reference not set", "ERROR");
        entry.Severity.Should().Be("ERROR");
    }

    [Fact]
    public void AllKnown_IncludesDefaults()
    {
        var all = ErrorCatalog.AllKnown().ToList();
        all.Should().Contain(e => e.Code == "ERR-999");
        all.Should().Contain(e => e.Code == "WRN-999");
    }
}
