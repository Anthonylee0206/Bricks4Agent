using StrategyWorker.Engine;

namespace Unit.Tests.Workers.Strategy;

/// <summary>
/// Kelly 部位 sizing 純數學(KellyPositionSizer)——確定性公式 f* = (b·p − q) / b。
///   - Compute:已知輸入 → 預期 fraction(含 break-even 與負期望夾 0)。
///   - FractionalKelly:quarter/half-Kelly 縮放。
///   - ClampPct:per-strategy max 配重夾限。
///   - RecommendedPct:全鏈組合(raw → fractional → cap)。
/// 算術全部手算對照;浮點不整除處用容差。
/// </summary>
public class KellyPositionSizerTests
{
    private const decimal Tol = 1e-9m;

    // ---- Compute:已知輸入正確 ----

    [Fact]
    public void Compute_KnownInput_ReturnsExactFraction()
    {
        // b = 2/1 = 2、p = 0.6、q = 0.4 → f* = (2·0.6 − 0.4)/2 = 0.8/2 = 0.4
        KellyPositionSizer.Compute(winRate: 0.6m, avgWin: 2m, avgLoss: 1m)
            .Should().Be(0.4m);
    }

    [Fact]
    public void Compute_AsymmetricPayoff_MatchesFormulaWithinTolerance()
    {
        // b = 8.2/5.1、p = 0.55 → f* = p − q·(avgLoss/avgWin)
        //   = 0.55 − 0.45·(5.1/8.2) = 0.55 − 2.295/8.2 ≈ 0.270121951
        KellyPositionSizer.Compute(winRate: 0.55m, avgWin: 8.2m, avgLoss: 5.1m)
            .Should().BeApproximately(0.2701219512m, 1e-9m);
    }

    [Fact]
    public void Compute_BreakEvenEdge_ReturnsZero()
    {
        // b = 1、p = 0.5、q = 0.5 → f* = (0.5 − 0.5)/1 = 0(零優勢、不下注)
        KellyPositionSizer.Compute(winRate: 0.5m, avgWin: 1m, avgLoss: 1m)
            .Should().Be(0m);
    }

    [Fact]
    public void Compute_NegativeExpectation_ClampedToZero()
    {
        // b = 1、p = 0.4、q = 0.6 → f* = (0.4 − 0.6)/1 = −0.2 → Math.Max(0, …) = 0
        KellyPositionSizer.Compute(winRate: 0.4m, avgWin: 1m, avgLoss: 1m)
            .Should().Be(0m);
    }

    [Fact]
    public void Compute_HighEdge_ReturnsLargeFraction()
    {
        // b = 3、p = 0.9、q = 0.1 → f* = (3·0.9 − 0.1)/3 = 2.6/3 ≈ 0.866666…
        KellyPositionSizer.Compute(winRate: 0.9m, avgWin: 3m, avgLoss: 1m)
            .Should().BeApproximately(0.8666666667m, 1e-9m);
    }

    [Theory]
    [InlineData(0)]   // p = 0:守門夾下限
    [InlineData(1)]   // p = 1:守門夾上限
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void Compute_WinRateOutOfOpenInterval_ReturnsZero(double winRate)
    {
        KellyPositionSizer.Compute((decimal)winRate, avgWin: 2m, avgLoss: 1m)
            .Should().Be(0m);
    }

    [Theory]
    [InlineData(0, 1)]    // avgWin = 0
    [InlineData(-1, 1)]   // avgWin < 0
    [InlineData(1, 0)]    // avgLoss = 0(避免除以零)
    [InlineData(1, -1)]   // avgLoss < 0
    public void Compute_NonPositivePayoff_ReturnsZero(double avgWin, double avgLoss)
    {
        KellyPositionSizer.Compute(winRate: 0.6m, avgWin: (decimal)avgWin, avgLoss: (decimal)avgLoss)
            .Should().Be(0m);
    }

    // ---- FractionalKelly:縮放 ----

    [Fact]
    public void FractionalKelly_QuarterKelly_ScalesByFraction()
    {
        // 0.4 × 0.25 = 0.1
        KellyPositionSizer.FractionalKelly(0.4m, fraction: 0.25m).Should().Be(0.1m);
    }

    [Fact]
    public void FractionalKelly_HalfKelly_ScalesByFraction()
    {
        // 0.4 × 0.5 = 0.2
        KellyPositionSizer.FractionalKelly(0.4m, fraction: 0.5m).Should().Be(0.2m);
    }

    [Fact]
    public void FractionalKelly_DefaultFraction_IsQuarterKelly()
    {
        // 預設 fraction = 0.25 → 0.8 × 0.25 = 0.2
        KellyPositionSizer.FractionalKelly(0.8m).Should().Be(0.2m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.25)]
    public void FractionalKelly_NonPositiveFraction_ReturnsZero(double fraction)
    {
        KellyPositionSizer.FractionalKelly(0.4m, (decimal)fraction).Should().Be(0m);
    }

    // ---- ClampPct:配重夾限 ----

    [Fact]
    public void ClampPct_AboveMax_ClampedToMax()
    {
        // 0.3 > max 0.2 → 0.2
        KellyPositionSizer.ClampPct(0.3m, min: 0m, max: 0.2m).Should().Be(0.2m);
    }

    [Fact]
    public void ClampPct_BelowMin_ClampedToMin()
    {
        // −0.1 < min 0 → 0
        KellyPositionSizer.ClampPct(-0.1m, min: 0m, max: 0.2m).Should().Be(0m);
    }

    [Fact]
    public void ClampPct_WithinRange_Unchanged()
    {
        // 0.15 落在 [0, 0.2] → 原值
        KellyPositionSizer.ClampPct(0.15m, min: 0m, max: 0.2m).Should().Be(0.15m);
    }

    [Fact]
    public void ClampPct_DefaultBounds_CapAt20Pct()
    {
        // 預設 max = 0.20 → 0.5 夾到 0.2
        KellyPositionSizer.ClampPct(0.5m).Should().Be(0.20m);
    }

    // ---- RecommendedPct:全鏈組合 ----

    [Fact]
    public void RecommendedPct_ChainsComputeFractionalAndClamp()
    {
        // raw f* = 0.4(b2/p0.6)→ quarter 0.1 → clamp(0,0.2) 不觸頂 → 0.1
        KellyPositionSizer.RecommendedPct(winRate: 0.6m, avgWin: 2m, avgLoss: 1m)
            .Should().Be(0.1m);
    }

    [Fact]
    public void RecommendedPct_HighEdge_CappedByMaxPct()
    {
        // raw f* = 2.6/3 ≈ 0.8667 → quarter ≈ 0.2167 > maxPct 0.2 → 夾到 0.2
        KellyPositionSizer.RecommendedPct(winRate: 0.9m, avgWin: 3m, avgLoss: 1m)
            .Should().Be(0.2m);
    }

    [Fact]
    public void RecommendedPct_NegativeExpectation_ReturnsZero()
    {
        // raw f* = 0(負期望)→ 0 × 0.25 = 0 → clamp = 0
        KellyPositionSizer.RecommendedPct(winRate: 0.4m, avgWin: 1m, avgLoss: 1m)
            .Should().Be(0m);
    }
}
