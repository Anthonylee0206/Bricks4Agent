using StrategyWorker.Engine;

namespace Unit.Tests.Workers.Strategy;

/// <summary>
/// 回撤感知部位 sizing(DrawdownAwareSizer)純數學:
///   - Linear:scalar = 1 − DD/maxDD,clamp [0,1]
///   - Polynomial:scalar = (1 − DD/maxDD)^power
///   - Step:離散 threshold 分階
///   - CurrentDdFromEquityCurve:從 equity curve 算當前 DD(從歷史 peak)
///   - Simulate:套 sizing 後 final/maxDD 的確定性案例
/// 全為確定性輸入;decimal 路徑用精確比較,經過 Math.Pow 的用容差。
/// </summary>
public class DrawdownAwareSizerTests
{
    private const decimal Tol = 1e-9m;

    // ---- LinearScale ----

    [Theory]
    [InlineData(0.05, 0.20, 0.75)]   // 1 − 0.25
    [InlineData(0.10, 0.20, 0.50)]   // 1 − 0.50
    [InlineData(0.15, 0.20, 0.25)]   // 1 − 0.75
    [InlineData(0.20, 0.20, 0.00)]   // 1 − 1.0 = 0(達最大回撤)
    public void LinearScale_KnownValues(decimal dd, decimal maxDd, decimal expected)
    {
        DrawdownAwareSizer.LinearScale(dd, maxDd).Should().Be(expected);
    }

    [Fact]
    public void LinearScale_ClampsToZero_WhenDdExceedsMax()
    {
        // 1 − 0.30/0.20 = 1 − 1.5 = −0.5 → clamp 到 0
        DrawdownAwareSizer.LinearScale(0.30m, 0.20m).Should().Be(0m);
    }

    [Fact]
    public void LinearScale_FullSize_OnZeroOrNegativeDrawdown()
    {
        DrawdownAwareSizer.LinearScale(0m, 0.20m).Should().Be(1m);       // 零回撤 → 全倉
        DrawdownAwareSizer.LinearScale(-0.05m, 0.20m).Should().Be(1m);   // 負回撤(權益創高)→ 全倉
    }

    [Fact]
    public void LinearScale_FullSize_WhenMaxDdNonPositive()
    {
        DrawdownAwareSizer.LinearScale(0.10m, 0m).Should().Be(1m);       // maxDd<=0 → 短路 1
        DrawdownAwareSizer.LinearScale(0.10m, -0.20m).Should().Be(1m);
    }

    // ---- PolynomialScale ----

    [Fact]
    public void PolynomialScale_DefaultPower2_KnownValues()
    {
        // frac = 1 − 0.05/0.20 = 0.75 → 0.75^2 = 0.5625
        ((double)DrawdownAwareSizer.PolynomialScale(0.05m, 0.20m)).Should().BeApproximately(0.5625, 1e-9);
        // frac = 1 − 0.10/0.20 = 0.5 → 0.5^2 = 0.25
        ((double)DrawdownAwareSizer.PolynomialScale(0.10m, 0.20m)).Should().BeApproximately(0.25, 1e-9);
    }

    [Theory]
    [InlineData(0.10, 0.20, 1.0, 0.5)]     // frac 0.5 ^1 = 0.5
    [InlineData(0.10, 0.20, 3.0, 0.125)]   // frac 0.5 ^3 = 0.125
    [InlineData(0.05, 0.20, 1.0, 0.75)]    // frac 0.75 ^1 = 0.75
    public void PolynomialScale_HonorsPowerArgument(decimal dd, decimal maxDd, decimal power, double expected)
    {
        ((double)DrawdownAwareSizer.PolynomialScale(dd, maxDd, power)).Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void PolynomialScale_ZeroAtAndBeyondMaxDrawdown()
    {
        // frac = 1 − 0.20/0.20 = 0 → 短路 0(不進 Math.Pow)
        DrawdownAwareSizer.PolynomialScale(0.20m, 0.20m).Should().Be(0m);
        // frac = 1 − 0.30/0.20 = −0.5 ≤ 0 → 0
        DrawdownAwareSizer.PolynomialScale(0.30m, 0.20m).Should().Be(0m);
    }

    [Fact]
    public void PolynomialScale_FullSize_OnZeroDrawdownOrNonPositiveMax()
    {
        DrawdownAwareSizer.PolynomialScale(0m, 0.20m).Should().Be(1m);     // 零回撤 → 全倉
        DrawdownAwareSizer.PolynomialScale(-0.1m, 0.20m).Should().Be(1m);  // 負回撤 → 全倉
        DrawdownAwareSizer.PolynomialScale(0.10m, 0m).Should().Be(1m);     // maxDd<=0 → 1
    }

    [Fact]
    public void PolynomialScale_IsStricterThanLinear_InMidRange()
    {
        // 同一中段 DD,poly(^2) 縮得比 linear 更兇(stop-loss 性質)
        var lin = DrawdownAwareSizer.LinearScale(0.10m, 0.20m);   // 0.50
        var poly = DrawdownAwareSizer.PolynomialScale(0.10m, 0.20m); // 0.25
        poly.Should().BeLessThan(lin);
    }

    // ---- StepScale (default tiers: 0.05→1.00, 0.10→0.75, 0.15→0.50, 0.20→0.25, else 0) ----

    [Theory]
    [InlineData(0.03, 1.00)]   // ≤0.05 第一階
    [InlineData(0.05, 1.00)]   // 邊界 ≤0.05 仍第一階(含上界)
    [InlineData(0.07, 0.75)]   // ≤0.10
    [InlineData(0.10, 0.75)]   // 邊界 ≤0.10
    [InlineData(0.12, 0.50)]   // ≤0.15
    [InlineData(0.15, 0.50)]   // 邊界 ≤0.15
    [InlineData(0.18, 0.25)]   // ≤0.20
    [InlineData(0.20, 0.25)]   // 邊界 ≤0.20
    [InlineData(0.25, 0.00)]   // 超過所有 threshold → 完全停
    public void StepScale_DefaultTiers_KnownValues(decimal dd, decimal expected)
    {
        DrawdownAwareSizer.StepScale(dd).Should().Be(expected);
    }

    [Fact]
    public void StepScale_FullSize_OnZeroOrNegativeDrawdown()
    {
        DrawdownAwareSizer.StepScale(0m).Should().Be(1m);
        DrawdownAwareSizer.StepScale(-0.05m).Should().Be(1m);
    }

    [Fact]
    public void StepScale_HonorsCustomTiers()
    {
        var tiers = new[]
        {
            (Threshold: 0.10m, Scalar: 0.80m),
            (Threshold: 0.50m, Scalar: 0.40m),
        };
        DrawdownAwareSizer.StepScale(0.05m, tiers).Should().Be(0.80m);  // ≤0.10
        DrawdownAwareSizer.StepScale(0.30m, tiers).Should().Be(0.40m);  // ≤0.50
        DrawdownAwareSizer.StepScale(0.60m, tiers).Should().Be(0m);     // 超過全部 → 0
    }

    // ---- CurrentDdFromEquityCurve ----

    [Fact]
    public void CurrentDdFromEquityCurve_PeakThenDrop()
    {
        // peak=120、current=90 → (120−90)/120 = 0.25
        DrawdownAwareSizer.CurrentDdFromEquityCurve(new[] { 100m, 120m, 90m }).Should().Be(0.25m);
    }

    [Fact]
    public void CurrentDdFromEquityCurve_ZeroWhenAtNewHigh()
    {
        // 一路向上、最後一根即 peak → DD 0
        DrawdownAwareSizer.CurrentDdFromEquityCurve(new[] { 100m, 110m, 120m }).Should().Be(0m);
    }

    [Fact]
    public void CurrentDdFromEquityCurve_KnownDropFraction()
    {
        // peak=100、current=80 → 0.20
        DrawdownAwareSizer.CurrentDdFromEquityCurve(new[] { 100m, 80m }).Should().Be(0.20m);
    }

    [Fact]
    public void CurrentDdFromEquityCurve_ZeroForTooFewPoints()
    {
        DrawdownAwareSizer.CurrentDdFromEquityCurve(new[] { 100m }).Should().Be(0m);  // count<2
        DrawdownAwareSizer.CurrentDdFromEquityCurve(Array.Empty<decimal>()).Should().Be(0m);
    }

    [Fact]
    public void CurrentDdFromEquityCurve_ZeroWhenPeakNonPositive()
    {
        // peak = max(−50,−100) = −50 ≤ 0 → 防呆回 0
        DrawdownAwareSizer.CurrentDdFromEquityCurve(new[] { -50m, -100m }).Should().Be(0m);
    }

    // ---- Simulate ----

    [Fact]
    public void Simulate_NoDrawdownCurve_AdjustedEqualsOriginal()
    {
        // 單調上升:每根的 currentDd=0 → scalar=1 → 調整後完全等於原始
        // [100,110,121]:每根 +10% → origFinal=121、origMaxDd=0
        var (origF, origDd, adjF, adjDd) =
            DrawdownAwareSizer.Simulate(new[] { 100m, 110m, 121m }, maxAcceptableDd: 0.20m, scaleMethod: "poly");

        origF.Should().Be(121m);
        origDd.Should().Be(0m);
        adjF.Should().BeApproximately(121m, Tol);   // 無回撤 → 不縮、等於原始
        adjDd.Should().Be(0m);
    }

    [Fact]
    public void Simulate_DrawdownAtMax_ZeroesOutRecoveryBar()
    {
        // [100,80,100]、poly、maxAcceptableDd=0.20:
        //   原始:i1 DD 0.20、i2 回 100 → origFinal=100、origMaxDd=0.20
        //   調整:i1 sizing 用前一根 DD=0 → scalar=1 → adj 100→80
        //         i2 用 DD=0.20 → poly frac=0 → scalar=0 → 回補報酬被歸零 → adj 停在 80
        //   ∴ adjFinal=80(未參與反彈)、adjMaxDd=0.20
        var (origF, origDd, adjF, adjDd) =
            DrawdownAwareSizer.Simulate(new[] { 100m, 80m, 100m }, maxAcceptableDd: 0.20m, scaleMethod: "poly");

        origF.Should().Be(100m);
        origDd.Should().Be(0.20m);
        adjF.Should().BeApproximately(80m, Tol);
        adjDd.Should().BeApproximately(0.20m, Tol);
    }

    [Fact]
    public void Simulate_ReturnsZeros_ForTooFewPoints()
    {
        DrawdownAwareSizer.Simulate(new[] { 100m }).Should().Be((0m, 0m, 0m, 0m));   // count<2 → 防呆
    }
}
