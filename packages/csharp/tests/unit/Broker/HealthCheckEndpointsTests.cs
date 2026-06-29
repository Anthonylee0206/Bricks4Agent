using Broker.Endpoints;
using FluentAssertions;
using Xunit;

namespace Unit.Tests.Broker;

/// <summary>
/// /api/v1/health/alerts 的事件型別過濾(IsPlatformHealthAlert)確定性測試。
/// 確保只有 HEALTH_* / WORKER_* 類觀測會出現在健康告警 feed,其餘稽核事件不混入。
/// </summary>
public class HealthCheckEndpointsTests
{
    [Theory]
    [InlineData("HEALTH_SCORE_CRITICAL", true)]
    [InlineData("HEALTH_DEGRADED", true)]
    [InlineData("WORKER_HEARTBEAT_LOST", true)]
    [InlineData("OBSERVATION_RECORDED", false)]
    [InlineData("EXECUTION_DENIED", false)]
    [InlineData("STATE_DIVERGENCE", false)]
    [InlineData("", false)]
    public void IsPlatformHealthAlert_KeepsOnlyHealthAndWorkerEvents(string eventType, bool expected)
    {
        HealthCheckEndpoints.IsPlatformHealthAlert(eventType).Should().Be(expected);
    }
}
