using Broker.Services.Observability;
using FluentAssertions;

namespace Unit.Tests.Broker.Observability;

public sealed class HttpStatusMetricsTests
{
    private static readonly DateTime T0 = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void QueryErrors_IsHalfOpen_AndOnlyKeepsErrorStatuses()
    {
        var metrics = new HttpStatusMetrics();

        metrics.Record("/api/v1/health/score", 200, T0);
        metrics.Record("/api/v1/health/score", 401, T0);
        metrics.Record("/api/v1/health/score", 401, T0.AddSeconds(5));
        metrics.Record("/api/v1/health/score", 401, T0.AddSeconds(10));   // 在時間窗之外（end 不含）

        var errors = metrics.QueryErrors(T0, T0.AddSeconds(10));

        errors.Should().ContainSingle()
            .Which.Should().Be(new HttpStatusCount("/api/v1/health/score", 401, 2));
        metrics.CountResponses(T0, T0.AddMinutes(1)).Should().Be(4);
    }

    [Fact]
    public void RequestsThatMatchNoRoute_ShareOneBucket_SoRandomPathsCannotGrowMemory()
    {
        var metrics = new HttpStatusMetrics();

        for (var i = 0; i < 50; i++)
            metrics.Record(routeTemplate: null, 404, T0);

        metrics.QueryErrors(T0, T0.AddSeconds(1)).Should().ContainSingle()
            .Which.Should().Be(new HttpStatusCount(HttpStatusMetrics.UnmatchedRoute, 404, 50));
    }

    [Fact]
    public void ErrorEntries_StopGrowingAtTheCap_AndTheDropsAreCounted()
    {
        var metrics = new HttpStatusMetrics(maxErrorEntries: 3);

        for (var i = 0; i < 5; i++)
            metrics.Record("/api/v1/x", 500, T0.AddSeconds(i));   // 每秒一組，第 4、5 組超過上限
        metrics.Record("/api/v1/x", 500, T0);                     // 已存在的組合照樣累加

        metrics.QueryErrors(T0, T0.AddMinutes(1)).Single().Count.Should().Be(4);
        metrics.DroppedErrors.Should().Be(2);
    }
}
