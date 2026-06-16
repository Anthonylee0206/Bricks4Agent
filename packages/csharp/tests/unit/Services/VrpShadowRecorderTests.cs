using Broker.Services;
using BrokerCore.Models;

namespace Unit.Tests.Services;

/// <summary>
/// VRP shadow recorder 純生命週期契約。無 I/O:釘死「何時開腿 / 何時結算 / 開腿與結算各填什麼」。
/// always-on、每 HorizonDays 一條 roll、冪等 Id、結算雙 PnL 走 VrpShadowMath。
/// </summary>
public class VrpShadowRecorderTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── ShouldOpenNewLeg(always-on、roll 邊界)──────────────────────

    [Fact]
    public void ShouldOpen_NoLeg_True()
        => VrpShadowRecorder.ShouldOpenNewLeg(T0, null, 30).Should().BeTrue();

    [Fact]
    public void ShouldOpen_WithinHorizon_False()
    {
        var leg = VrpShadowRecorder.BuildNewLeg("BTC", T0, 50m, 60000m, 1m, 30, 1000m, 50m);
        VrpShadowRecorder.ShouldOpenNewLeg(T0.AddDays(15), leg, 30).Should().BeFalse();   // 還在 roll 內
    }

    [Fact]
    public void ShouldOpen_AtOrPastHorizon_True()
    {
        var leg = VrpShadowRecorder.BuildNewLeg("BTC", T0, 50m, 60000m, 1m, 30, 1000m, 50m);
        VrpShadowRecorder.ShouldOpenNewLeg(T0.AddDays(30), leg, 30).Should().BeTrue();     // roll 邊界
        VrpShadowRecorder.ShouldOpenNewLeg(T0.AddDays(31), leg, 30).Should().BeTrue();
    }

    // ── BuildNewLeg(冪等 Id、roll 視窗、進場快照)────────────────────

    [Fact]
    public void BuildNewLeg_SetsIdempotentIdAndWindow()
    {
        var leg = VrpShadowRecorder.BuildNewLeg("BTC", T0, 48.5m, 60000m, 0.8m, 30, 2000m, 50m);
        long entryMs = new DateTimeOffset(T0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        leg.Id.Should().Be($"vrp:BTC:{entryMs}");
        leg.RollEndTime.Should().Be(T0.AddDays(30));
        leg.DvolEntry.Should().Be(48.5m);
        leg.SizeScalar.Should().Be(0.8m);
        leg.Shadow.Should().BeTrue();
        leg.SettledAt.Should().BeNull();          // 開腿時未結算
    }

    [Fact]
    public void BuildNewLeg_SameRollBoundary_SameId()
    {
        // 冪等:同幣同進場時刻 → 同 Id(同 roll 不重開)
        var a = VrpShadowRecorder.BuildNewLeg("BTC", T0, 50m, 60000m, 1m, 30, 1000m, 50m);
        var b = VrpShadowRecorder.BuildNewLeg("BTC", T0, 51m, 61000m, 1m, 30, 1000m, 50m);
        a.Id.Should().Be(b.Id);
    }

    // ── ShouldSettle ──────────────────────────────────────────────

    [Fact]
    public void ShouldSettle_UnsettledPastRollEnd_True()
    {
        var leg = VrpShadowRecorder.BuildNewLeg("BTC", T0, 50m, 60000m, 1m, 30, 1000m, 50m);
        VrpShadowRecorder.ShouldSettle(leg, T0.AddDays(30)).Should().BeTrue();
        VrpShadowRecorder.ShouldSettle(leg, T0.AddDays(29)).Should().BeFalse();   // roll 還沒到
    }

    [Fact]
    public void ShouldSettle_AlreadySettled_False()
    {
        var leg = VrpShadowRecorder.BuildNewLeg("BTC", T0, 50m, 60000m, 1m, 30, 1000m, 50m);
        VrpShadowRecorder.ApplySettlement(leg, 30m, T0.AddDays(30));
        VrpShadowRecorder.ShouldSettle(leg, T0.AddDays(40)).Should().BeFalse();   // 已結算不重算
    }

    // ── ApplySettlement(雙 PnL、走 VrpShadowMath)────────────────────

    [Fact]
    public void ApplySettlement_ComputesDualPnl_Profit()
    {
        var leg = VrpShadowRecorder.BuildNewLeg("BTC", T0, 50m, 60000m, 1m, 30, 1000m, 50m);
        VrpShadowRecorder.ApplySettlement(leg, 30m, T0.AddDays(30));   // 隱含50 > 實現30 → +20

        leg.RealizedVol.Should().Be(30m);
        leg.PnlPctNaked.Should().Be(20m);
        leg.PnlPctCapped.Should().Be(20m);
        leg.SettledAt.Should().Be(T0.AddDays(30));
        leg.CloseReason.Should().Be("settled");
    }

    [Fact]
    public void ApplySettlement_VolSpike_CappedSavesTail()
    {
        // 實現爆量:裸賣 -136% 是不可部署幻覺、封頂救到 -50%(sketch §2 核心對照)
        var leg = VrpShadowRecorder.BuildNewLeg("BTC", T0, 40m, 60000m, 1m, 30, 1000m, 50m);
        VrpShadowRecorder.ApplySettlement(leg, 176m, T0.AddDays(30));

        leg.PnlPctNaked.Should().Be(-136m);
        leg.PnlPctCapped.Should().Be(-50m);
    }
}
