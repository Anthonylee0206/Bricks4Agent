using Broker.Services;

namespace Unit.Tests.Services;

/// <summary>
/// 真錢風控純函式契約:finding C(當日 PnL% 尺)、D(熔斷 scope key 多用戶隔離)、J(scale-in 加碼深度)。
/// 全部無 I/O、無 env、無時間 → 純輸入→純輸出,釘死「該怎麼算」的真錢決策。
/// </summary>
public class AutoTraderRiskHelpersTests
{
    // ── finding C：ComputeDayPnlPct（當日 PnL%）──────────────────────

    [Theory]
    [InlineData(0, 0, 0)]      // open<=0(新帳戶)→ 0、不誤觸熔斷
    [InlineData(50, -5, 0)]    // open<0(壞資料)→ 0、除零/負分母防呆
    [InlineData(100, 100, 0)]  // 持平 → 0
    [InlineData(94, 100, -6)]  // 跌 6%
    [InlineData(110, 100, 10)] // 漲 10%
    public void ComputeDayPnlPct_Contract(decimal current, decimal open, decimal expectedPct)
        => AutoTraderService.ComputeDayPnlPct(current, open).Should().Be(expectedPct);

    [Fact]
    public void ComputeDayPnlPct_EquityScalePinsTheBug()
    {
        // finding C 釘死:抱浮虧倉、wallet balance 持平(已實現 0)但 equity 因浮虧跌 6%。
        // 用 equity(94) 算得到 -6%(會觸發 r16 -6% 熔斷);用 balance(100) 算得到 0(熔斷永不觸發=原 bug)。
        AutoTraderService.ComputeDayPnlPct(94m, 100m).Should().Be(-6m, "餵 equity 才看得到浮虧");
        AutoTraderService.ComputeDayPnlPct(100m, 100m).Should().Be(0m, "餵 wallet balance 看不到浮虧(原 bug)");
    }

    // ── finding D：BuildCbScopeKey（熔斷 scope、多用戶隔離）─────────────

    [Fact]
    public void BuildCbScopeKey_EmptyOwner_FallsBackToDashboard()
    {
        AutoTraderService.BuildCbScopeKey(null, "bingx").Should().Be("prn_dashboard:bingx");
        AutoTraderService.BuildCbScopeKey("", "bingx").Should().Be("prn_dashboard:bingx");
    }

    [Fact]
    public void BuildCbScopeKey_DistinctOwnersSameExchange_DistinctKeys()
    {
        // 多用戶核心:同 exchange、不同 owner → 不同 key(否則 equity peak 互相污染、A 的 DD 擋到 B)
        var a = AutoTraderService.BuildCbScopeKey("prn_userA", "bingx");
        var b = AutoTraderService.BuildCbScopeKey("prn_userB", "bingx");
        a.Should().Be("prn_userA:bingx");
        b.Should().Be("prn_userB:bingx");
        a.Should().NotBe(b);
    }

    // ── finding J：ResolveScaleInExisting（加碼深度)──────────────────

    [Theory]
    [InlineData(1, 2, 2)]  // hedge 合併成 1 筆、累計加碼 2 次 → 用 2(核心:讓門檻隨深度累進)
    [InlineData(1, 0, 1)]  // broker 重啟丟計數、倉還在 → max 兜底回 1(還原舊行為、不放鬆風控)
    [InlineData(0, 0, 0)]  // 全新倉 → 0(門檻=base)
    [InlineData(3, 1, 3)]  // one-way 分筆回報 rowCount 較高 → 取 rowCount、不低估
    public void ResolveScaleInExisting_Contract(int rowCount, int recorded, int expected)
        => AutoTraderService.ResolveScaleInExisting(rowCount, recorded).Should().Be(expected);
}
