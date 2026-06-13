using BrokerCore.Trading;

namespace Unit.Tests.Services;

/// <summary>
/// PnlAggregator.Aggregate 純算法——對一組 realized PnL 數列做績效聚合:
/// 筆數/勝敗計數、總和、勝率%、平均賺/賠、利潤因子。
/// 驗證已知輸入的數值正確與邊界(空集合、單筆、正負/零混合、無虧損 ∞、全平)。
/// </summary>
public class PnlAggregatorTests
{
    [Fact]
    public void Empty_ReturnsAllZero()
    {
        var s = PnlAggregator.Aggregate(Array.Empty<decimal>());
        s.TradeCount.Should().Be(0);
        s.WinCount.Should().Be(0);
        s.LoseCount.Should().Be(0);
        s.RealizedPnlSum.Should().Be(0m);
        s.WinRatePct.Should().Be(0m);   // total==0 → guard 回 0、不除以零
        s.AvgWin.Should().Be(0m);
        s.AvgLoss.Should().Be(0m);
        s.ProfitFactor.Should().Be(0m); // 兩邊都 0 → 0
    }

    [Fact]
    public void SingleWin_CountsAndAverages()
    {
        var s = PnlAggregator.Aggregate(new[] { 50m });
        s.TradeCount.Should().Be(1);
        s.WinCount.Should().Be(1);
        s.LoseCount.Should().Be(0);
        s.RealizedPnlSum.Should().Be(50m);
        s.WinRatePct.Should().Be(100.0m);
        s.AvgWin.Should().Be(50m);
        s.AvgLoss.Should().Be(0m);
        // 有獲利但無虧損 → ProfitFactor 哨兵 99.99(代表 ∞)
        s.ProfitFactor.Should().Be(99.99m);
    }

    [Fact]
    public void SingleLoss_OnlyLossSideSet()
    {
        var s = PnlAggregator.Aggregate(new[] { -30m });
        s.TradeCount.Should().Be(1);
        s.WinCount.Should().Be(0);
        s.LoseCount.Should().Be(1);
        s.RealizedPnlSum.Should().Be(-30m);
        s.WinRatePct.Should().Be(0.0m);
        s.AvgWin.Should().Be(0m);
        s.AvgLoss.Should().Be(-30m);  // 平均虧損保留負號
        // winSum==0 且 lossSum<0 → 0 / |loss| = 0
        s.ProfitFactor.Should().Be(0m);
    }

    [Fact]
    public void MixedWinsAndLosses_FullStats()
    {
        // 5 筆:+100、+50、-40、-10、+25
        // wins=3 (winSum=175)、loses=2 (lossSum=-50)、sum=125
        var s = PnlAggregator.Aggregate(new[] { 100m, 50m, -40m, -10m, 25m });
        s.TradeCount.Should().Be(5);
        s.WinCount.Should().Be(3);
        s.LoseCount.Should().Be(2);
        s.RealizedPnlSum.Should().Be(125m);
        s.WinRatePct.Should().Be(60.0m);                  // 3/5 = 60.0
        s.AvgWin.Should().Be(58.3333m);                   // 175/3 = 58.33333… → round(4) = 58.3333
        s.AvgLoss.Should().Be(-25m);                      // -50/2
        s.ProfitFactor.Should().Be(3.5m);                 // 175 / |−50| = 3.5
    }

    [Fact]
    public void Zeros_CountAsTradesButNotWinOrLoss()
    {
        // 0 入 total 但不入 wins/loses;勝率分母含 0 筆
        var s = PnlAggregator.Aggregate(new[] { 0m, 0m, 40m, -20m });
        s.TradeCount.Should().Be(4);
        s.WinCount.Should().Be(1);
        s.LoseCount.Should().Be(1);
        s.RealizedPnlSum.Should().Be(20m);
        s.WinRatePct.Should().Be(25.0m);  // 1 win / 4 total = 25.0(零交易稀釋勝率)
        s.AvgWin.Should().Be(40m);
        s.AvgLoss.Should().Be(-20m);
        s.ProfitFactor.Should().Be(2m);   // 40 / 20
    }

    [Fact]
    public void AllZeros_NoWinsNoLosses_ProfitFactorZero()
    {
        var s = PnlAggregator.Aggregate(new[] { 0m, 0m, 0m });
        s.TradeCount.Should().Be(3);
        s.WinCount.Should().Be(0);
        s.LoseCount.Should().Be(0);
        s.RealizedPnlSum.Should().Be(0m);
        s.WinRatePct.Should().Be(0.0m);   // 0 wins / 3 total
        s.AvgWin.Should().Be(0m);
        s.AvgLoss.Should().Be(0m);
        s.ProfitFactor.Should().Be(0m);   // winSum==0 且 lossSum==0 → 0
    }

    [Fact]
    public void WinRate_RoundsToOneDecimal()
    {
        // 1 win / 3 total = 33.333…% → round(1) = 33.3
        var s = PnlAggregator.Aggregate(new[] { 10m, -5m, -5m });
        s.WinRatePct.Should().Be(33.3m);
    }

    [Fact]
    public void RealizedPnlSum_RoundsToFourDecimals()
    {
        // 0.00005 + 0.00005 + 0.123456 = 0.123556 → round 到 4 位 = 0.1236
        var s = PnlAggregator.Aggregate(new[] { 0.00005m, 0.00005m, 0.123456m });
        s.RealizedPnlSum.Should().Be(0.1236m);
    }

    [Fact]
    public void ProfitFactor_RoundsToThreeDecimals()
    {
        // winSum=10、lossSum=-3 → 10/3 = 3.3333… → round(3) = 3.333
        var s = PnlAggregator.Aggregate(new[] { 10m, -3m });
        s.ProfitFactor.Should().Be(3.333m);
    }

    [Fact]
    public void AvgLoss_StaysNegative_WithMultipleLosses()
    {
        // 全虧損 → 無獲利、ProfitFactor 0;AvgLoss = (-60)/3 = -20
        var s = PnlAggregator.Aggregate(new[] { -10m, -20m, -30m });
        s.WinCount.Should().Be(0);
        s.LoseCount.Should().Be(3);
        s.AvgWin.Should().Be(0m);
        s.AvgLoss.Should().Be(-20m);
        s.WinRatePct.Should().Be(0.0m);
        s.ProfitFactor.Should().Be(0m);
    }

    [Theory]
    [InlineData(new[] { 1.0, 1.0, 1.0, 1.0 }, 100.0)]   // 全勝
    [InlineData(new[] { -1.0, -1.0 }, 0.0)]              // 全敗
    [InlineData(new[] { 2.0, -1.0 }, 50.0)]              // 一勝一敗
    public void WinRatePct_MatchesExpected(double[] pnls, double expectedPct)
    {
        var s = PnlAggregator.Aggregate(pnls.Select(p => (decimal)p));
        s.WinRatePct.Should().Be((decimal)expectedPct);
    }
}
