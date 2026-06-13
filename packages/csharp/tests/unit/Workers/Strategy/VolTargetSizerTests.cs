using StrategyWorker.Engine;

namespace Unit.Tests.Workers.Strategy;

/// <summary>
/// Vol-targeting sizer 純數學(VolTargetSizer)——固定「風險」而非「名目」:
///   scalar = clamp(target_vol / realized_vol, min, max)。
/// 低 vol 期 scalar > 1(放大)、高 vol 期 scalar < 1(縮)、極端被 clamp 夾住;
/// 零/無效 vol 視為中性(scalar = 1、不放大)。final = kelly × scalar。
/// 浮點用容差;clamp 邊界值自己重算確認。
/// </summary>
public class VolTargetSizerTests
{
    private const decimal Tol = 1e-9m;

    // ---- ComputeScalar:已知輸入 → scalar ----

    [Fact]
    public void ComputeScalar_LowVol_AmplifiesAboveOne()
    {
        // realized 40% << target 60% → 60/40 = 1.5(在 [0.3,2.0] 內、不夾)
        var s = VolTargetSizer.ComputeScalar(realizedVol: 0.40m, targetVol: 0.60m);
        s.Should().BeApproximately(1.5m, Tol);
    }

    [Fact]
    public void ComputeScalar_VolEqualsTarget_IsOne()
    {
        // realized == target → scalar 正好 1.0(不縮不放)
        var s = VolTargetSizer.ComputeScalar(realizedVol: 0.60m, targetVol: 0.60m);
        s.Should().BeApproximately(1.0m, Tol);
    }

    [Fact]
    public void ComputeScalar_HighVol_ShrinksBelowOne()
    {
        // realized 120% >> target 60% → 60/120 = 0.5(在範圍內、保命縮一半)
        var s = VolTargetSizer.ComputeScalar(realizedVol: 1.20m, targetVol: 0.60m);
        s.Should().BeApproximately(0.5m, Tol);
    }

    [Fact]
    public void ComputeScalar_ExtremeVol_ClampsToMin()
    {
        // realized 300% → raw 60/300 = 0.2 < 下限 0.3 → 夾到 0.3(極端 crash 不繼續縮)
        var s = VolTargetSizer.ComputeScalar(realizedVol: 3.00m, targetVol: 0.60m);
        s.Should().BeApproximately(0.3m, Tol);
    }

    [Fact]
    public void ComputeScalar_AtMinBoundary_NotClampedFurther()
    {
        // realized 200% → raw 60/200 = 0.3 == 下限 → 留在 0.3
        var s = VolTargetSizer.ComputeScalar(realizedVol: 2.00m, targetVol: 0.60m);
        s.Should().BeApproximately(0.3m, Tol);
    }

    [Fact]
    public void ComputeScalar_VeryLowVol_ClampsToMax_DoesNotAmplifyUnbounded()
    {
        // realized 10% → raw 60/10 = 6.0 > 上限 2.0 → 夾到 2.0(低 vol 不無限放大)
        var s = VolTargetSizer.ComputeScalar(realizedVol: 0.10m, targetVol: 0.60m);
        s.Should().BeApproximately(2.0m, Tol);
    }

    [Fact]
    public void ComputeScalar_ZeroRealizedVol_IsNeutralOne()
    {
        // realized <= 0(資料不足)→ 直接回 1(中性、不放大、不除以零)
        VolTargetSizer.ComputeScalar(realizedVol: 0m, targetVol: 0.60m).Should().BeApproximately(1.0m, Tol);
        VolTargetSizer.ComputeScalar(realizedVol: -0.5m, targetVol: 0.60m).Should().BeApproximately(1.0m, Tol);
    }

    [Fact]
    public void ComputeScalar_ZeroTargetVol_IsNeutralOne()
    {
        // target <= 0 → 回 1(無目標、不調整)
        VolTargetSizer.ComputeScalar(realizedVol: 0.60m, targetVol: 0m).Should().BeApproximately(1.0m, Tol);
    }

    [Fact]
    public void ComputeScalar_CustomBounds_AreHonored()
    {
        // 自訂 [0.5,1.5]:raw 0.5(60/120)在內 → 0.5
        VolTargetSizer.ComputeScalar(1.20m, 0.60m, min: 0.5m, max: 1.5m).Should().BeApproximately(0.5m, Tol);
        // raw 0.2(60/300)< 自訂下限 0.5 → 夾 0.5
        VolTargetSizer.ComputeScalar(3.00m, 0.60m, min: 0.5m, max: 1.5m).Should().BeApproximately(0.5m, Tol);
        // raw 6.0(60/10)> 自訂上限 1.5 → 夾 1.5
        VolTargetSizer.ComputeScalar(0.10m, 0.60m, min: 0.5m, max: 1.5m).Should().BeApproximately(1.5m, Tol);
    }

    [Fact]
    public void ComputeScalar_Monotonic_HigherVolGivesSmallerScalar()
    {
        // 在未夾區間內,realized 越高 scalar 越小(嚴格遞減)
        var lo = VolTargetSizer.ComputeScalar(0.50m, 0.60m);  // 1.2
        var mid = VolTargetSizer.ComputeScalar(0.80m, 0.60m); // 0.75
        var hi = VolTargetSizer.ComputeScalar(1.00m, 0.60m);  // 0.6
        lo.Should().BeGreaterThan(mid);
        mid.Should().BeGreaterThan(hi);
    }

    // ---- FinalPct = kellyPct × scalar ----

    [Fact]
    public void FinalPct_InsufficientData_ScalarOne_ReturnsKellyUnchanged()
    {
        // closes < lookback+1 → realized vol = 0 → scalar 1 → final == kelly
        var closes = new List<decimal> { 100m, 101m, 99m };
        var final = VolTargetSizer.FinalPct(closes, kellyPct: 0.10m, targetVol: 0.60m, lookback: 30);
        final.Should().BeApproximately(0.10m, Tol);
    }

    [Fact]
    public void FinalPct_ConstantPrices_ZeroVariance_ScalarOne()
    {
        // 常數價序列 → 所有 log return = 0 → variance 0 → vol 0 → scalar 1 → final == kelly
        var closes = Enumerable.Repeat(100m, 40).ToList();
        var final = VolTargetSizer.FinalPct(closes, kellyPct: 0.20m, targetVol: 0.60m, lookback: 30);
        final.Should().BeApproximately(0.20m, Tol);
    }

    [Fact]
    public void FinalPct_HigherVolSeries_ShrinksMoreThanLowerVolSeries()
    {
        // 同 kelly:高 vol 序列 final 應 <= 低 vol 序列 final(vol-target 縮放方向正確)
        var calm = MakeAlternating(0.01m, 40);     // ±1% 每根 → 低 realized vol
        var wild = MakeAlternating(0.10m, 40);     // ±10% 每根 → 高 realized vol
        var calmFinal = VolTargetSizer.FinalPct(calm, kellyPct: 0.10m, targetVol: 0.60m, lookback: 30);
        var wildFinal = VolTargetSizer.FinalPct(wild, kellyPct: 0.10m, targetVol: 0.60m, lookback: 30);
        wildFinal.Should().BeLessThan(calmFinal);
    }

    // ---- AnnualizedRealizedVol:邊界 ----

    [Fact]
    public void AnnualizedRealizedVol_NullOrTooFewBars_ReturnsZero()
    {
        VolTargetSizer.AnnualizedRealizedVol(null!, lookback: 30).Should().Be(0m);
        // 剛好 lookback 根(需 lookback+1)→ 0
        VolTargetSizer.AnnualizedRealizedVol(Enumerable.Repeat(100m, 30).ToList(), lookback: 30).Should().Be(0m);
    }

    [Fact]
    public void AnnualizedRealizedVol_FewerThanFiveReturns_ReturnsZero()
    {
        // lookback 4 → 只有 4 個 returns < 5 → 守門回 0
        var closes = Enumerable.Repeat(100m, 5).ToList();
        VolTargetSizer.AnnualizedRealizedVol(closes, lookback: 4).Should().Be(0m);
    }

    [Fact]
    public void AnnualizedRealizedVol_ConstantPrices_ZeroVolatility()
    {
        // 常數價 → 零變異 → 年化 vol = 0
        var closes = Enumerable.Repeat(100m, 40).ToList();
        VolTargetSizer.AnnualizedRealizedVol(closes, lookback: 30).Should().Be(0m);
    }

    [Fact]
    public void AnnualizedRealizedVol_KnownAlternatingReturns_MatchesHandComputed()
    {
        // ±1% 交替(price ×1.01 / ÷1.01):每根 log return = ±ln(1.01),mean=0。
        // variance = sum(r^2)/(n-1) = n·r^2/(n-1)、daily std = |r|·sqrt(n/(n-1))、
        // 年化 = daily std · sqrt(365)。n = lookback = 30 個 returns。
        var closes = MakeAlternating(0.01m, 31);   // 31 closes → 正好 30 returns
        var vol = VolTargetSizer.AnnualizedRealizedVol(closes, lookback: 30);

        int n = 30;
        double r = Math.Abs(Math.Log(1.01));
        double dailyStd = r * Math.Sqrt((double)n / (n - 1));
        var expected = (decimal)(dailyStd * Math.Sqrt(365));

        vol.Should().BeApproximately(expected, 1e-6m);
        vol.Should().BeGreaterThan(0m);
    }

    // ---- helper:交替乘除產生確定性對稱 log-return 序列 ----
    // 起始 100,偶數步 ×(1+pct)、奇數步 ÷(1+pct);log returns 為 +ln(1+pct)/−ln(1+pct) 交替,mean=0。
    private static List<decimal> MakeAlternating(decimal pct, int count)
    {
        var up = 1m + pct;
        var list = new List<decimal>(count) { 100m };
        for (int i = 1; i < count; i++)
            list.Add((i % 2 == 1) ? list[i - 1] * up : list[i - 1] / up);
        return list;
    }
}
