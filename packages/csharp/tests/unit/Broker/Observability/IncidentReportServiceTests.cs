using Broker.Services.Observability;
using BrokerCore.Models;
using BrokerCore.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unit.Tests.Broker.Observability;

/// <summary>整條流程：收集 → 規則 → 敘述 → 保存 → 觀測事件（真的 SQLite、假的 LLM）。</summary>
public sealed class IncidentReportServiceTests : IDisposable
{
    private readonly TempBrokerDb _temp = TempBrokerDb.Create();
    private readonly StubLlm _llm = new();
    private readonly IncidentReportOptions _options = new();
    private readonly IncidentReportStore _store;
    private readonly ObservationService _observations;

    public IncidentReportServiceTests()
    {
        _store = new IncidentReportStore(_temp.Db, NullLogger<IncidentReportStore>.Instance);
        _observations = new ObservationService(_temp.Db, new AuditService(_temp.Db));
    }

    public void Dispose() => _temp.Dispose();

    private IncidentReportService Service()
    {
        var builder = new AnomalyDigestBuilder(
            _temp.Db,
            new ServiceCollection().AddSingleton(new LlmProxyMetrics()).BuildServiceProvider(),
            new HttpStatusMetrics(),
            NullLogger<AnomalyDigestBuilder>.Instance);
        return new IncidentReportService(
            _options,
            builder,
            new IncidentNarrator(_llm, NullLogger<IncidentNarrator>.Instance),
            _store,
            _observations,
            NullLogger<IncidentReportService>.Instance);
    }

    [Fact]
    public async Task Findings_AreSaved_Narrated_AndRaisedAsObservations()
    {
        var end = DateTime.UtcNow;
        var start = end.AddMinutes(-15);
        for (var i = 0; i < 6; i++)
            _temp.AddExecution("deploy.run", ExecutionState.Denied, end.AddMinutes(-5), "Capability not granted");
        _temp.AddAudit("PLAN_DEADLOCKED", end.AddMinutes(-3));
        _llm.Reply = _ => new LlmChatResult { Content = "總結。R3 拒絕暴增。R5 計畫死結。", Model = "stub" };

        var report = await Service().RunAsync(start, end, IncidentReportTriggers.Manual);

        report.Findings.Select(f => f.RuleId).Should().BeEquivalentTo(["R3", "R5"]);
        report.MaxSeverity.Should().Be(IncidentSeverity.Critical);
        report.NarrativeStatus.Should().Be("ok");

        var saved = _store.Get(report.ReportId);
        saved.Should().NotBeNull();
        saved!.Findings.Should().HaveCount(2);
        saved.WindowStart.Should().Be(report.WindowStart);

        var alerts = _observations.GetAlerts(start, ObservationSeverity.Warning);
        alerts.Select(a => a.EventType).Should().Contain(["INCIDENT_R3", "INCIDENT_R5"]);
        alerts.Single(a => a.EventType == "INCIDENT_R5").Severity.Should().Be(ObservationSeverity.Critical);
    }

    [Fact]
    public async Task QuietWindow_IsStillSaved_WithoutCallingTheLlm()
    {
        var end = DateTime.UtcNow;

        var report = await Service().RunAsync(end.AddMinutes(-15), end, IncidentReportTriggers.Manual);

        report.Findings.Should().BeEmpty();
        report.MaxSeverity.Should().Be("none");
        report.NarrativeStatus.Should().Be("skipped_no_findings");
        _llm.Calls.Should().BeEmpty();
        report.Coverage.RulesRun.Should().NotBeEmpty("報告要寫明看過哪些東西");
        _store.GetRecent(5).Should().ContainSingle();
    }

    [Fact]
    public async Task NarrationFailure_DoesNotLoseTheFindings()
    {
        var end = DateTime.UtcNow;
        _temp.AddAudit("PLAN_FAILED", end.AddMinutes(-1));
        _llm.Reply = _ => throw new HttpRequestException("connection refused");

        var report = await Service().RunAsync(end.AddMinutes(-15), end, IncidentReportTriggers.Manual);

        report.NarrativeStatus.Should().Be("failed");
        _store.Get(report.ReportId)!.Findings.Should().ContainSingle(f => f.RuleId == "R5");
    }

    [Fact]
    public async Task NarrationDisabledByConfig_IsRecordedAsDisabled()
    {
        _options.Narrate = false;
        var end = DateTime.UtcNow;
        _temp.AddAudit("PLAN_FAILED", end.AddMinutes(-1));

        var report = await Service().RunAsync(end.AddMinutes(-15), end, IncidentReportTriggers.Manual);

        report.NarrativeStatus.Should().Be("disabled");
        _llm.Calls.Should().BeEmpty();
    }

    [Fact]
    public void Baseline_UsesScheduledReportsOnly_AndTreatsMissingCountsAsZero()
    {
        var t = new DateTime(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
        IncidentReport Scheduled(int index, Dictionary<string, double> metrics) => new()
        {
            Trigger = IncidentReportTriggers.Scheduled,
            WindowStart = t.AddMinutes(15 * index),
            WindowEnd = t.AddMinutes(15 * (index + 1)),
            Metrics = metrics,
        };
        var history = new List<IncidentReport>
        {
            new() { Trigger = IncidentReportTriggers.Manual, WindowStart = t, WindowEnd = t.AddMinutes(1), Metrics = new() { ["denied:x"] = 500 } },
            Scheduled(0, new() { ["denied:x"] = 30 }),
            Scheduled(1, new()),   // 這一窗沒有拒絕
        };

        var baseline = IncidentReportService.BuildBaseline(history, 4);

        baseline.HasHistory.Should().BeTrue();
        // (30/15 + 0/15) / 2 = 1 筆／分鐘；手動報告的 500 不算
        baseline.PerMinute["denied:x"].Should().BeApproximately(1.0, 1e-9);
        baseline.Expected("denied:x", 15).Should().BeApproximately(15, 1e-9);
        baseline.Expected("never-seen", 15).Should().Be(0);
        IncidentReportService.BuildBaseline([history[0]], 4).HasHistory.Should().BeFalse();
    }

    [Fact]
    public async Task ConcurrentRuns_AreSerialised_AndEachGetsItsOwnVersion()
    {
        var service = Service();
        var end = DateTime.UtcNow;

        await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(i => service.RunAsync(end.AddMinutes(-15 - i), end, IncidentReportTriggers.Manual)));

        _store.GetRecent(10).Select(r => r.ReportId).Should().OnlyHaveUniqueItems().And.HaveCount(5);
    }
}
