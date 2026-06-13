using BrokerCore.Trading;

namespace Unit.Tests.Services;

/// <summary>
/// CorrelationGuard 純數學測試 — daily-return Pearson correlation 與「新倉 vs 已開倉」最大相關判定。
/// 鎖住已知數值(完全同向=1、完全反向=-1、平盤=0)、保守邊界(空/不等長/<11根/零變異/非正價→0)。
/// </summary>
public class CorrelationGuardTests
{
    // 產生一段固定 close 序列:從 start 起、依 returns 逐根套用 close[i] = close[i-1] * (1 + r)。
    private static decimal[] FromReturns(decimal start, params decimal[] returns)
    {
        var closes = new decimal[returns.Length + 1];
        closes[0] = start;
        for (int i = 0; i < returns.Length; i++)
            closes[i + 1] = closes[i] * (1m + returns[i]);
        return closes;
    }

    // 10 個非平凡 returns(11 closes)= 剛好過 n<11 門檻。
    private static readonly decimal[] Pattern =
        { 0.10m, -0.10m, 0.10m, -0.10m, 0.10m, -0.10m, 0.10m, -0.10m, 0.10m, -0.10m };

    [Fact]
    public void Pearson_IdenticalSeries_IsPerfectPositiveOne()
    {
        // 兩序列 returns 完全相同 → r = +1
        var a = FromReturns(100m, Pattern);
        var b = FromReturns(50m, Pattern);   // 不同起點、相同 return 形狀
        var r = CorrelationGuard.PearsonOfReturns(a, b);
        ((double)r).Should().BeApproximately(1.0, 1e-4);
    }

    [Fact]
    public void Pearson_MirroredReturns_IsPerfectNegativeMinusOne()
    {
        // B 的每一根 return 恰為 A 的相反數 → r = -1
        var mirror = new decimal[Pattern.Length];
        for (int i = 0; i < Pattern.Length; i++) mirror[i] = -Pattern[i];
        var a = FromReturns(100m, Pattern);
        var b = FromReturns(100m, mirror);
        var r = CorrelationGuard.PearsonOfReturns(a, b);
        ((double)r).Should().BeApproximately(-1.0, 1e-4);
    }

    [Fact]
    public void Pearson_OrthogonalReturns_IsNearZero()
    {
        // A 與 B 的去中心化 return 內積為 0 → r ≈ 0。
        // A returns: +1,-1,+1,-1...(均值 0);B returns: +1,+1,-1,-1...(均值 0、與 A 正交)
        var ar = new decimal[10];
        var br = new decimal[10];
        for (int i = 0; i < 10; i++)
        {
            ar[i] = (i % 2 == 0) ? 0.05m : -0.05m;          // + - + - ...
            br[i] = ((i / 2) % 2 == 0) ? 0.05m : -0.05m;    // + + - - + + - - ...
        }
        var a = FromReturns(100m, ar);
        var b = FromReturns(100m, br);
        var r = CorrelationGuard.PearsonOfReturns(a, b);
        ((double)r).Should().BeApproximately(0.0, 1e-4);
    }

    [Fact]
    public void Pearson_NullInputs_ReturnZero()
    {
        var a = FromReturns(100m, Pattern);
        CorrelationGuard.PearsonOfReturns(null!, a).Should().Be(0m);
        CorrelationGuard.PearsonOfReturns(a, null!).Should().Be(0m);
        CorrelationGuard.PearsonOfReturns(null!, null!).Should().Be(0m);
    }

    [Theory]
    [InlineData(1)]   // 1 close → 0 returns
    [InlineData(10)]  // 10 closes → 9 returns、仍 < 11 門檻
    public void Pearson_TooFewSamples_ReturnZero(int len)
    {
        // 不足 11 根 close → 保守回 0(不擋)
        var full = FromReturns(100m, Pattern);            // 11 根
        var shortA = full.Take(len).ToArray();
        var shortB = FromReturns(100m, Pattern).Take(len).ToArray();
        CorrelationGuard.PearsonOfReturns(shortA, shortB).Should().Be(0m);
    }

    [Fact]
    public void Pearson_UnequalLength_UsesShorterAndGuardsThreshold()
    {
        // 取兩者較短長度;若較短側 < 11 根 → 0
        var longSeries = FromReturns(100m, Pattern);                          // 11 根
        var tiny = new decimal[] { 100m, 101m, 102m };                        // 3 根 → min=3 < 11 → 0
        CorrelationGuard.PearsonOfReturns(longSeries, tiny).Should().Be(0m);
        CorrelationGuard.PearsonOfReturns(tiny, longSeries).Should().Be(0m);
    }

    [Fact]
    public void Pearson_FlatSeries_ZeroVariance_ReturnZero()
    {
        // 全平盤 → return 全 0 → 變異為 0 → 回 0(避免除以 0)
        var flat = Enumerable.Repeat(100m, 12).ToArray();
        var moving = FromReturns(100m, Pattern);
        CorrelationGuard.PearsonOfReturns(flat, moving).Should().Be(0m);
        CorrelationGuard.PearsonOfReturns(flat, flat).Should().Be(0m);
    }

    [Fact]
    public void Pearson_NonPositiveClose_ReturnZero()
    {
        // 報酬計算分母若遇 <=0 的 close → 保守回 0
        var bad = new decimal[] { 100m, 0m, 100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m, 100m };
        var good = FromReturns(100m, Pattern);
        CorrelationGuard.PearsonOfReturns(bad, good).Should().Be(0m);
    }

    [Fact]
    public void Pearson_RoundedToFourDecimals()
    {
        // 回傳值不應超過 4 位小數(實作 Math.Round(r, 4))
        var a = FromReturns(100m, Pattern);
        var b = FromReturns(100m, new decimal[]
            { 0.10m, -0.08m, 0.12m, -0.09m, 0.11m, -0.07m, 0.13m, -0.10m, 0.09m, -0.11m });
        var r = CorrelationGuard.PearsonOfReturns(a, b);
        decimal.Round(r, 4).Should().Be(r);
        r.Should().BeInRange(-1m, 1m);
    }

    [Fact]
    public void ComputeMax_EmptyOrNullMap_ReturnsZeroAndNull()
    {
        var newSym = FromReturns(100m, Pattern);
        var empty = new Dictionary<string, IReadOnlyList<decimal>>();
        var (corr1, sym1) = CorrelationGuard.ComputeMaxCorrelation(newSym, empty);
        corr1.Should().Be(0m);
        sym1.Should().BeNull();

        var (corr2, sym2) = CorrelationGuard.ComputeMaxCorrelation(newSym, null!);
        corr2.Should().Be(0m);
        sym2.Should().BeNull();
    }

    [Fact]
    public void ComputeMax_PicksHighestAbsoluteCorrelationSymbol()
    {
        var newSym = FromReturns(100m, Pattern);

        var mirror = new decimal[Pattern.Length];
        for (int i = 0; i < Pattern.Length; i++) mirror[i] = -Pattern[i];

        var orthoR = new decimal[10];
        for (int i = 0; i < 10; i++) orthoR[i] = ((i / 2) % 2 == 0) ? 0.05m : -0.05m;

        var map = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["SAME"]   = FromReturns(100m, Pattern),   // r = +1
            ["MIRROR"] = FromReturns(100m, mirror),    // r = -1 → abs 1、但 SAME 先到、abs 相等不取代
            ["ORTHO"]  = FromReturns(100m, orthoR),    // r ≈ 0
        };

        var (maxCorr, maxSym) = CorrelationGuard.ComputeMaxCorrelation(newSym, map);
        ((double)maxCorr).Should().BeApproximately(1.0, 1e-4);  // 取絕對值
        maxSym.Should().Be("SAME");                              // 第一個達到最大 abs 的 symbol
    }

    [Fact]
    public void ComputeMax_NegativeCorrelation_CountsByAbsoluteValue()
    {
        // 唯一一個是強負相關 → MaxCorr 仍回正的 |r|=1、symbol 指向它
        var newSym = FromReturns(100m, Pattern);
        var mirror = new decimal[Pattern.Length];
        for (int i = 0; i < Pattern.Length; i++) mirror[i] = -Pattern[i];

        var map = new Dictionary<string, IReadOnlyList<decimal>>
        {
            ["INVERSE"] = FromReturns(100m, mirror),
        };

        var (maxCorr, maxSym) = CorrelationGuard.ComputeMaxCorrelation(newSym, map);
        ((double)maxCorr).Should().BeApproximately(1.0, 1e-4);
        maxCorr.Should().BeGreaterThan(0m);
        maxSym.Should().Be("INVERSE");
    }
}
