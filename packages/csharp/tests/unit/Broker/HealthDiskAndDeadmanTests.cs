using Broker.Services;
using FluentAssertions;
using Xunit;

namespace Unit.Tests.Broker;

/// <summary>
/// 磁碟水位判定(EvaluateDiskLevel)與 snapshot 管線死人開關(ComputeSnapshotStaleness)的確定性測試。
/// 鎖住兩個守衛的邊界:磁碟=百分比×絕對值取較嚴重者、缺資料不懲罰;
/// dead-man=讀取時計算、冷啟動(無 snapshot)不假警報、超過 3×tick 才算停擺。
/// </summary>
public class HealthDiskAndDeadmanTests
{
    private const long GB = 1024L * 1024 * 1024;

    // ── EvaluateDiskLevel ────────────────────────────────────────────

    [Fact]
    public void Disk_PlentyFree_Ok()
        => HealthScoreSnapshotService.EvaluateDiskLevel(100 * GB, 50 * GB)
            .Should().Be(HealthScoreSnapshotService.DiskLevel.Ok);

    [Fact]
    public void Disk_LowPercent_Warning()
        // 100GB 中剩 10GB = 10% < 15%(絕對值 10GB 還夠)→ Warning 由百分比觸發
        => HealthScoreSnapshotService.EvaluateDiskLevel(100 * GB, 10 * GB)
            .Should().Be(HealthScoreSnapshotService.DiskLevel.Warning);

    [Fact]
    public void Disk_LowAbsolute_Warning_EvenOnBigDisk()
        // 2TB 大磁碟剩 4GB = 0.2%…會直接 Critical;改測絕對值窗:800GB 剩 4.5GB=0.56%→Critical 由 pct;
        // 絕對值單獨觸發的窗:total 25GB、free 4.5GB = 18%(pct ok)但 <5GB → Warning
        => HealthScoreSnapshotService.EvaluateDiskLevel(25 * GB, (long)(4.5 * GB))
            .Should().Be(HealthScoreSnapshotService.DiskLevel.Warning);

    [Fact]
    public void Disk_CriticalPercent()
        // 100GB 剩 6GB = 6% < 7% → Critical
        => HealthScoreSnapshotService.EvaluateDiskLevel(100 * GB, 6 * GB)
            .Should().Be(HealthScoreSnapshotService.DiskLevel.Critical);

    [Fact]
    public void Disk_CriticalAbsolute_EvenWhenPctOk()
        // total 12GB、free 1.5GB = 12.5%(> 7% pct ok)但 < 2GB 絕對地板 → Critical
        => HealthScoreSnapshotService.EvaluateDiskLevel(12 * GB, (long)(1.5 * GB))
            .Should().Be(HealthScoreSnapshotService.DiskLevel.Critical);

    [Fact]
    public void Disk_NoData_Ok_NotPunished()
        // 抓不到磁碟資訊(total<=0)→ 缺資料不懲罰(與健康分數同哲學)
        => HealthScoreSnapshotService.EvaluateDiskLevel(0, 0)
            .Should().Be(HealthScoreSnapshotService.DiskLevel.Ok);

    // ── ComputeSnapshotStaleness(dead-man)──────────────────────────

    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);
    private static readonly DateTime Now = new(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Deadman_Fresh_NotStalled()
    {
        var (stalled, age) = HealthScoreSnapshotService.ComputeSnapshotStaleness(
            Now.AddMinutes(-6), Now, Tick);   // 6 分鐘 < 3×5 分鐘
        stalled.Should().BeFalse();
        age.Should().BeApproximately(360, 1);
    }

    [Fact]
    public void Deadman_Stale_Stalled()
    {
        var (stalled, age) = HealthScoreSnapshotService.ComputeSnapshotStaleness(
            Now.AddMinutes(-20), Now, Tick);  // 20 分鐘 > 15 分鐘
        stalled.Should().BeTrue();
        age.Should().BeApproximately(1200, 1);
    }

    [Fact]
    public void Deadman_ExactBoundary_NotStalled()
        // 恰好 3×tick = 不算停擺(嚴格大於才算、避免臨界抖動)
        => HealthScoreSnapshotService.ComputeSnapshotStaleness(Now.AddMinutes(-15), Now, Tick)
            .Stalled.Should().BeFalse();

    [Fact]
    public void Deadman_NoSnapshotEver_NoColdStartFalseAlarm()
    {
        var (stalled, age) = HealthScoreSnapshotService.ComputeSnapshotStaleness(null, Now, Tick);
        stalled.Should().BeFalse("冷啟動從未有 snapshot 不該假警報");
        age.Should().BeNull();
    }

    [Fact]
    public void Deadman_ClockDrift_ClampedToZero()
    {
        // snapshot 時間在未來(時鐘漂移)→ age clamp 0、不停擺
        var (stalled, age) = HealthScoreSnapshotService.ComputeSnapshotStaleness(
            Now.AddMinutes(5), Now, Tick);
        stalled.Should().BeFalse();
        age.Should().Be(0);
    }
}
