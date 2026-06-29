using Broker.Services;
using FluentAssertions;
using FunctionPool.Container;
using Xunit;

namespace Unit.Tests.Broker;

/// <summary>
/// HealthScoreService.ResolveContainerStats 的確定性測試（純函數、無需 mock docker / registry）。
/// 鎖住舊版 container_name 前綴比對的回歸 bug：同前綴 worker 互搶容器 + 底線式 worker_id 失配。
/// </summary>
public class HealthScoreServiceTests
{
    private static ContainerStats Stat(string id, string name, double cpu, double mem) => new()
    {
        ContainerId = id,
        ContainerName = name,
        CpuPercent = cpu,
        MemoryPercent = mem,
    };

    [Fact]
    public void Resolve_AuthoritativeMapping_AttributesEachWorkerToItsOwnContainer()
    {
        // 兩個同前綴 worker；舊版 Split('-')[0]="trading" + FirstOrDefault(Contains) 會把兩者
        // 都對到第一個容器 → wkr-2 被算成 wkr-1 的低負載。精確對應必須各歸各的。
        var stats = new List<ContainerStats>
        {
            Stat("aaaa11112222", "b4a-trading-wkr-1", cpu: 5,  mem: 5),
            Stat("bbbb33334444", "b4a-trading-wkr-2", cpu: 95, mem: 95),
        };
        var map = new Dictionary<string, string>
        {
            ["trading-wkr-1"] = "aaaa11112222",
            ["trading-wkr-2"] = "bbbb33334444",
        };

        HealthScoreService.ResolveContainerStats(stats, "trading-wkr-1", map)!.ContainerId.Should().Be("aaaa11112222");
        HealthScoreService.ResolveContainerStats(stats, "trading-wkr-2", map)!.ContainerId.Should().Be("bbbb33334444");
    }

    [Fact]
    public void Resolve_ShortVsFullContainerId_Matches()
    {
        // docker stats 回短 id（12 字元），ManagedContainer 可能存完整 64 字元 id。
        var stats = new List<ContainerStats> { Stat("abc123def456", "worker-x", 10, 10) };
        var map = new Dictionary<string, string> { ["wkr_x"] = "abc123def456789000aaaabbbbccccddddeeee" };

        HealthScoreService.ResolveContainerStats(stats, "wkr_x", map)!.ContainerId.Should().Be("abc123def456");
    }

    [Fact]
    public void Resolve_UnderscoreWorkerId_ResolvesViaMapping()
    {
        // 舊版痛點：底線式 worker_id「wkr_01HABC」用 Split('-') 切不開、整串 Contains 必失配。
        var stats = new List<ContainerStats> { Stat("c0ffee001122", "b4a-file-worker", 20, 20) };
        var map = new Dictionary<string, string> { ["wkr_01HABC"] = "c0ffee001122" };

        HealthScoreService.ResolveContainerStats(stats, "wkr_01HABC", map)!.ContainerId.Should().Be("c0ffee001122");
    }

    [Fact]
    public void Resolve_NoMapping_FallsBackToExactName()
    {
        // 無權威對應（NoOpContainerManager）→ 退保守 container_name 比對。
        var stats = new List<ContainerStats> { Stat("d00d00d00d00", "line-worker", 30, 30) };
        var empty = new Dictionary<string, string>();

        HealthScoreService.ResolveContainerStats(stats, "line-worker", empty)!.ContainerId.Should().Be("d00d00d00d00");
    }

    [Fact]
    public void Resolve_NoMatch_ReturnsNull()
    {
        var stats = new List<ContainerStats> { Stat("eeee0000ffff", "some-other-container", 40, 40) };
        var empty = new Dictionary<string, string>();

        HealthScoreService.ResolveContainerStats(stats, "wkr_unknown", empty).Should().BeNull();
    }

    // ── 計分子函式(純函數、確定性)──────────────────────────────────────

    [Theory]
    [InlineData(10, 100, "fresh")]
    [InlineData(45, 50, "stale")]
    [InlineData(90, 0, "lost")]
    public void ScoreHeartbeat_BandsBySeconds(int seconds, int expectedScore, string expectedLabel)
    {
        var s = HealthScoreService.ScoreHeartbeat(TimeSpan.FromSeconds(seconds));
        s.Score.Should().Be(expectedScore);
        s.Label.Should().Be(expectedLabel);
    }

    [Fact]
    public void ScoreDispatch_NoData_ReturnsNull()
        => HealthScoreService.ScoreDispatch((0, 0)).Should().BeNull();

    [Theory]
    [InlineData(100, 0, 100, "excellent")]
    [InlineData(97, 3, 70, "marginal")]
    [InlineData(85, 15, 40, "degraded")]
    [InlineData(50, 50, 10, "failing")]
    public void ScoreDispatch_BandsBySuccessRate(int ok, int fail, int expectedScore, string expectedLabel)
    {
        var s = HealthScoreService.ScoreDispatch((ok, fail))!;
        s.Score.Should().Be(expectedScore);
        s.Label.Should().Be(expectedLabel);
    }

    [Fact]
    public void WeightedAvg_AllPresent_WeightsCorrectly()
        => HealthScoreService.WeightedAvg((100, 0.30), (50, 0.40), (0, 0.30)).Should().Be(50);

    [Fact]
    public void WeightedAvg_SkipsNullComponents_AndRenormalizes()
        => HealthScoreService.WeightedAvg((100, 0.30), (null, 0.40), (80, 0.30)).Should().Be(90);

    [Fact]
    public void WeightedAvg_NoData_DefaultsTo100()
        => HealthScoreService.WeightedAvg((null, 0.30), (null, 0.40)).Should().Be(100);

    [Theory]
    [InlineData(80, "healthy")]
    [InlineData(79, "degraded")]
    [InlineData(50, "degraded")]
    [InlineData(49, "critical")]
    public void StatusFor_Thresholds(int score, string expected)
        => HealthScoreService.StatusFor(score).Should().Be(expected);
}
