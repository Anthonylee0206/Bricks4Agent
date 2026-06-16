using FluentAssertions;
using QuoteWorker.Handlers;
using QuoteWorker.Models;
using Xunit;

namespace Unit.Tests.Workers.Quote;

/// <summary>
/// QuoteOhlcvHandler.AlignDvol 的純函式測試:把 Deribit DVOL 隱含波動序列 as-of join 到每根 K 線
/// (向前填充)。VRP / 波動 carry 的隱含波動維度靠這條接線。比照 AlignFunding/Oi/RetailLs 範式。
/// </summary>
public class DvolAlignTests
{
    private static OhlcvBar Bar(int day) => new()
    {
        Symbol = "BTC", Interval = "1d",
        OpenTime = new DateTime(2024, 1, day, 0, 0, 0, DateTimeKind.Utc),
        Open = 100, High = 101, Low = 99, Close = 100, Volume = 1000,
    };

    private static DvolPoint Dvol(int day, int hour, decimal value) => new()
    {
        Symbol = "BTC",
        SampleTime = new DateTime(2024, 1, day, hour, 0, 0, DateTimeKind.Utc),
        DvolValue = value,
    };

    [Fact]
    public void ForwardFills_NearestPriorDvol()
    {
        var bars = new List<OhlcvBar> { Bar(1), Bar(2), Bar(3) };
        // DVOL:day1 00:00 = 55、day2 16:00 = 60(晚於 day2 bar 的 00:00)
        var dvol = new List<DvolPoint> { Dvol(1, 0, 55m), Dvol(2, 16, 60m) };

        var merged = QuoteOhlcvHandler.AlignDvol(bars, dvol);

        merged.Should().HaveCount(3);
        merged[0].DvolValue.Should().Be(55m);   // day1 bar:取 day1 00:00
        merged[1].DvolValue.Should().Be(55m);   // day2 00:00 bar:day2 16:00 還沒發生 → 仍是 day1
        merged[2].DvolValue.Should().Be(60m);   // day3 bar:day2 16:00 已發生
    }

    [Fact]
    public void BarsBeforeAnyDvol_GetNull()
    {
        var bars = new List<OhlcvBar> { Bar(1), Bar(2) };
        var dvol = new List<DvolPoint> { Dvol(2, 0, 55m) };  // 第一筆 DVOL 在 day2

        var merged = QuoteOhlcvHandler.AlignDvol(bars, dvol);

        merged[0].DvolValue.Should().BeNull();   // day1 早於任何 DVOL → null(strategy 端降級)
        merged[1].DvolValue.Should().Be(55m);
    }

    [Fact]
    public void NoDvol_AllNull()
        => QuoteOhlcvHandler.AlignDvol(
                new List<OhlcvBar> { Bar(1), Bar(2) }, new List<DvolPoint>())
            .Should().OnlyContain(m => m.DvolValue == null);
}
