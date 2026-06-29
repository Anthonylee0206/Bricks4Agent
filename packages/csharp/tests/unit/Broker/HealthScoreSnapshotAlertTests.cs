using Broker.Services;
using FluentAssertions;
using Xunit;

namespace Unit.Tests.Broker;

/// <summary>
/// HealthScoreSnapshotService 的 critical 告警狀態機(NextCriticalAlertState)確定性測試。
/// 鎖住 edge-trigger:達門檻才觸發一次、持續不重複灌、恢復才 reset。
/// </summary>
public class HealthScoreSnapshotAlertTests
{
    private const int Threshold = 3;

    // 把一串 critical 旗標餵進狀態機,回傳每個 tick 是否觸發告警。
    private static List<bool> Run(params bool[] criticalSeq)
    {
        var state = new HealthScoreSnapshotService.CriticalAlertState(0, false);
        var fires = new List<bool>();
        foreach (var c in criticalSeq)
        {
            var (next, fire) = HealthScoreSnapshotService.NextCriticalAlertState(state, c, Threshold);
            state = next;
            fires.Add(fire);
        }
        return fires;
    }

    [Fact]
    public void BelowThreshold_NeverFires()
        => Run(true, true).Should().AllSatisfy(f => f.Should().BeFalse());

    [Fact]
    public void FiresExactlyAtThreshold_OnceOnly()
        => Run(true, true, true, true, true).Should().Equal(false, false, true, false, false);

    [Fact]
    public void RecoveryResets_ThenCanFireAgain()
        => Run(true, true, true, false, true, true, true)
            .Should().Equal(false, false, true, false, false, false, true);

    [Fact]
    public void IntermittentCritical_NeverReachesThreshold()
        => Run(true, false, true, false, true, false).Should().AllSatisfy(f => f.Should().BeFalse());

    [Fact]
    public void NonCritical_ResetsCountAndFlag()
    {
        var (next, fire) = HealthScoreSnapshotService.NextCriticalAlertState(
            new HealthScoreSnapshotService.CriticalAlertState(5, true), critical: false, Threshold);

        fire.Should().BeFalse();
        next.ConsecutiveCritical.Should().Be(0);
        next.AlertActive.Should().BeFalse();
    }
}
