using Broker.Services;

namespace Unit.Tests.Services;

/// <summary>
/// VRP / 波動 carry shadow 純量測函式契約。無 I/O、釘死「shadow 怎麼算」:
/// 賣方收隱含、賠實現,淨 = 隱含 − 實現(vega 慣例);封頂把下檔封到 −capPct。
/// </summary>
public class VrpShadowMathTests
{
    // ── RealizedVolAnnualizedPct ──────────────────────────────────────

    [Fact]
    public void RealizedVol_TooFewBars_Zero()
    {
        VrpShadowMath.RealizedVolAnnualizedPct(new List<decimal>()).Should().Be(0m);
        VrpShadowMath.RealizedVolAnnualizedPct(new List<decimal> { 100m }).Should().Be(0m);
    }

    [Fact]
    public void RealizedVol_FlatSeries_Zero()
        => VrpShadowMath.RealizedVolAnnualizedPct(new List<decimal> { 100m, 100m, 100m, 100m })
            .Should().Be(0m);

    [Fact]
    public void RealizedVol_PositiveForMovingSeries()
        => VrpShadowMath.RealizedVolAnnualizedPct(new List<decimal> { 100m, 102m, 99m, 103m, 98m, 101m })
            .Should().BeGreaterThan(0m);

    [Fact]
    public void RealizedVol_KnownReturns_AnnualizesBySqrt365()
    {
        // closes 100→101→100:r1=ln(1.01)、r2=-ln(1.01)、mean=0;
        // sample var(n-1=1)= 2·ln(1.01)²;sd=√2·|ln1.01|;年化 = sd·√365·100
        var closes = new List<decimal> { 100m, 101m, 100m };
        double l = Math.Log(1.01);
        double expected = Math.Sqrt(2 * l * l) * Math.Sqrt(365.0) * 100.0;
        ((double)VrpShadowMath.RealizedVolAnnualizedPct(closes)).Should().BeApproximately(expected, 0.5);
    }

    [Fact]
    public void RealizedVol_SkipsNonPositiveCloses_NoThrow()
        => VrpShadowMath.RealizedVolAnnualizedPct(new List<decimal> { 100m, 0m, 102m })
            .Should().BeGreaterThanOrEqualTo(0m);

    // ── PnlNakedPct(vega 慣例:size × (隱含 − 實現))────────────────────

    [Theory]
    [InlineData(50, 30, 1.0, 20)]    // 隱含>實現 → 收溢酬賺
    [InlineData(50, 50, 1.0, 0)]     // 持平
    [InlineData(40, 140, 1.0, -100)] // 實現爆量 → 大負(裸賣左尾)
    [InlineData(50, 30, 0.5, 10)]    // size 0.5 → 半倉
    public void PnlNaked_VegaConvention(decimal dvol, decimal realized, double size, decimal expected)
        => VrpShadowMath.PnlNakedPct(dvol, realized, (decimal)size).Should().Be(expected);

    // ── PnlCappedPct(下檔封到 −capPct)────────────────────────────────

    [Theory]
    [InlineData(20, 50, 20)]      // 正報酬不封
    [InlineData(-30, 50, -30)]    // 虧 30 < cap → 不封
    [InlineData(-100, 50, -50)]   // 裸賣 -100 → 封到 -50(condor 可部署)
    [InlineData(-50, 50, -50)]    // 邊界
    public void PnlCapped_FloorsDownside(decimal naked, decimal cap, decimal expected)
        => VrpShadowMath.PnlCappedPct(naked, cap).Should().Be(expected);

    [Fact]
    public void NakedVsCapped_ReproducesResearchDistinction()
    {
        // 同一視窗:裸賣 -136% 是「不可部署幻覺」、封頂 -50% 是可部署值(sketch §2 的核心對照)
        var naked = VrpShadowMath.PnlNakedPct(40m, 176m, 1.0m);
        naked.Should().Be(-136m);
        VrpShadowMath.PnlCappedPct(naked, 50m).Should().Be(-50m);
    }
}
