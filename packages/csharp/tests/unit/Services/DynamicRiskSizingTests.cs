using Broker.Services;

namespace Unit.Tests.Services;

/// <summary>
/// finding G — 動態風險 sizing 純算法(ComputeDynamicRiskSizing)。
/// per-trade max_loss = balance × riskPct%、notional = allowedRisk / SL%,
/// 夾在組合風險預算與「保證金硬上限 balance × leverage × 0.95」之內(之前 dynamic 路徑漏了這個 cap)。
/// </summary>
public class DynamicRiskSizingTests
{
    // 預設:balance 1000、mark 100、SL 5%、無已開倉風險、riskPct 2%、組合上限 6%、5x
    private static AutoTraderService.DynamicRiskSizingResult Run(
        decimal balance = 1000m, decimal mark = 100m, decimal slPct = 5m,
        decimal existingRisk = 0m, decimal riskPct = 2m, decimal maxPortfolioRiskPct = 6m, decimal lev = 5m)
        => AutoTraderService.ComputeDynamicRiskSizing(balance, mark, slPct, existingRisk, riskPct, maxPortfolioRiskPct, lev);

    [Fact]
    public void Basic_NotionalEqualsRiskDividedBySlPct()
    {
        // perTradeMax = 1000×2% = 20;notional = 20/(5%) = 400;marginCap 5x = 4750 → 不夾;qty = 400/100 = 4
        var r = Run();
        r.Applicable.Should().BeTrue();
        r.BudgetExhausted.Should().BeFalse();
        r.MarginClamped.Should().BeFalse();
        r.AllowedNotional.Should().Be(400m);
        r.Qty.Should().Be(4m);
    }

    [Fact]
    public void MarginCap_ClampsWhenSmallSlLowLeverage()
    {
        // ★ finding G regression lock:SL 0.5% + 1x → notional = 20/0.005 = 4000、
        // 但 marginCap = 1000×1×0.95 = 950 → 夾到 950、qty = 9.5(沒 cap 會送 4000 名目被交易所拒單)
        var r = Run(slPct: 0.5m, lev: 1m);
        r.MarginClamped.Should().BeTrue();
        r.AllowedNotional.Should().Be(950m);
        r.Qty.Should().Be(9.5m);
    }

    [Fact]
    public void NoClamp_WhenHighLeverageHeadroom()
    {
        // SL 0.5% + 5x → marginCap = 4750 > notional 4000 → 不夾
        var r = Run(slPct: 0.5m, lev: 5m);
        r.MarginClamped.Should().BeFalse();
        r.AllowedNotional.Should().Be(4000m);
        r.Qty.Should().Be(40m);
    }

    [Fact]
    public void MarginCap_UsesAtLeast1x_WhenLeverageZero()
    {
        // lev 0 → Math.Max(lev,1)=1 → marginCap = 950;SL 0.5% notional 4000 → 夾到 950
        var r = Run(slPct: 0.5m, lev: 0m);
        r.MarginClamped.Should().BeTrue();
        r.AllowedNotional.Should().Be(950m);
    }

    [Fact]
    public void PortfolioBudget_CapsAgainstExistingRisk()
    {
        // 組合上限 6%×1000=60;已開倉風險 50 → 剩 10;perTradeMax 20 → 取 min=10;notional=10/5%=200;qty=2
        var r = Run(existingRisk: 50m);
        r.BudgetExhausted.Should().BeFalse();
        r.AllowedRisk.Should().Be(10m);
        r.AllowedNotional.Should().Be(200m);
        r.Qty.Should().Be(2m);
    }

    [Fact]
    public void BudgetExhausted_WhenExistingRiskMeetsMax()
    {
        // 已開倉風險 60 ≥ 組合上限 60 → 預算用完、skip
        var r = Run(existingRisk: 60m);
        r.Applicable.Should().BeTrue();
        r.BudgetExhausted.Should().BeTrue();
        r.Qty.Should().Be(0m);
    }

    [Fact]
    public void NoPortfolioCap_AllowsFullPerTrade()
    {
        // maxPortfolioRiskPct=0 → 不限組合;已開倉再多也不影響 per-trade
        var r = Run(existingRisk: 9999m, maxPortfolioRiskPct: 0m);
        r.BudgetExhausted.Should().BeFalse();
        r.AllowedRisk.Should().Be(20m);
    }

    [Fact]
    public void NotApplicable_WhenBalanceOrMarkOrSlInvalid()
    {
        Run(balance: 0m).Applicable.Should().BeFalse();
        Run(balance: -10m).Applicable.Should().BeFalse();
        Run(mark: 0m).Applicable.Should().BeFalse();
        Run(slPct: 0m).Applicable.Should().BeFalse();
    }
}
