using Broker.Services;
using Broker.Services.Observability;
using FluentAssertions;

namespace Unit.Tests.Broker.Observability;

/// <summary>
/// 規則層的單元測試：不碰資料庫、不呼叫 LLM，逐條檢查門檻的邊界。
/// 每個測試從一個「完全正常」的時間窗出發，只改一個地方。
/// </summary>
public sealed class AnomalyRulesTests
{
    private static readonly DateTime Start = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddMinutes(15);
    private static readonly IncidentRuleThresholds Defaults = new();

    private static IncidentWindowData QuietWindow() => new()
    {
        WindowStart = Start,
        WindowEnd = End,
        HostStartedAt = Start.AddHours(-2),
        PoolEnabled = true,
        Health = new HealthNow(100, "healthy", 2),
        SnapshotInterval = TimeSpan.FromMinutes(5),
        LatestSnapshotAt = End.AddMinutes(-2),
        HttpMetricsAvailable = true,
        HttpMetricsStartedAt = Start.AddHours(-2),
        Llm = new LlmWindowStats(0, 0, Start.AddHours(-2), false),
    };

    private static AnomalyRules.Evaluation Evaluate(IncidentWindowData data, AnomalyRules.Baseline? baseline = null)
        => AnomalyRules.Evaluate(data, baseline ?? AnomalyRules.Baseline.None, Defaults);

    private static AnomalyRules.Baseline History(params (string Key, double PerMinute)[] perMinute)
        => new(true, perMinute.ToDictionary(p => p.Key, p => p.PerMinute));

    [Fact]
    public void QuietWindow_HasNoFindings_RunsEveryRule_AndStillListsBlindSpots()
    {
        var result = Evaluate(QuietWindow());

        result.Findings.Should().BeEmpty();
        result.Coverage.RulesRun.Should().BeEquivalentTo(AnomalyRules.AllRules);
        result.Coverage.RulesNotApplicable.Should().BeEmpty();
        result.BlindSpots.Should().HaveCountGreaterThanOrEqualTo(5, "沒看到的東西也要寫出來");
    }

    [Fact]
    public void PoolDisabled_MarksWorkerRulesNotApplicable_InsteadOfSilentlyPassing()
    {
        var data = QuietWindow();
        data.PoolEnabled = false;
        data.Health = null;

        var result = Evaluate(data);

        result.Coverage.RulesNotApplicable.Keys.Should().Contain(["R1", "R11", "R12"]);
        result.Coverage.RulesRun.Should().NotContain(["R1", "R11", "R12"]);
        result.BlindSpots.Should().Contain(s => s.Contains("Function Pool 未啟用"));
    }

    [Fact]
    public void R1_HeartbeatLost_IsWarning_AndCriticalWhenNoWorkerIsLeft()
    {
        var data = QuietWindow();
        data.HeartbeatLost = [new HeartbeatLostInfo("wkr_a", Start.AddMinutes(3))];
        Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R1")
            .Which.Severity.Should().Be(IncidentSeverity.Warning);

        data.Health = new HealthNow(0, HealthScoreService.NoWorkersStatus, 0);
        Evaluate(data).Findings.Single(f => f.RuleId == "R1").Severity.Should().Be(IncidentSeverity.Critical);
    }

    [Theory]
    [InlineData(17, 3, null)]                        // 3/20 = 15%，未達 20%
    [InlineData(0, 2, null)]                         // 失敗率 100% 但只有 2 筆，未達最少 3 筆
    [InlineData(7, 3, IncidentSeverity.Warning)]     // 3/10 = 30%
    [InlineData(4, 6, IncidentSeverity.Critical)]    // 6/10 = 60%
    public void R2_ExecutionFailures_RespectMinimumCountAndRate(int succeeded, int failed, string? expectedSeverity)
    {
        var data = QuietWindow();
        data.Executions = [new CapabilityExecutionStats("file.write", succeeded, failed, 0, ["req_1"], [])];

        var r2 = Evaluate(data).Findings.Where(f => f.RuleId == "R2").ToList();

        if (expectedSeverity is null)
            r2.Should().BeEmpty();
        else
            r2.Should().ContainSingle().Which.Severity.Should().Be(expectedSeverity);
    }

    [Fact]
    public void R3_Denials_UseAbsoluteMinimumWithoutHistory_AndSpikeFactorWithHistory()
    {
        var data = QuietWindow();
        data.Executions = [new CapabilityExecutionStats("file.write", 0, 0, 5, ["req_1"], ["Capability not granted（5）"])];

        // 沒有歷史：只看最少 5 筆
        var finding = Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R3").Subject;
        finding.Detail.Should().Contain("Capability not granted");
        finding.Baseline.Should().BeNull();

        // 平常每分鐘就有 1 筆拒絕（15 分鐘約 15 筆）：5 筆不算暴增
        Evaluate(data, History(("denied:file.write", 1.0))).Findings.Should().NotContain(f => f.RuleId == "R3");

        // 有歷史、但平常是 0：5 筆就是暴增
        Evaluate(data, History(("other", 1.0))).Findings.Should().Contain(f => f.RuleId == "R3");

        data.Executions = [new CapabilityExecutionStats("file.write", 0, 0, 4, [], [])];
        Evaluate(data).Findings.Should().NotContain(f => f.RuleId == "R3");
    }

    [Fact]
    public void R4_StuckApprovals_AreOldOrExpired_AndFreshOnesAreIgnored()
    {
        var data = QuietWindow();
        data.PendingApprovals = [new PendingApprovalInfo("apr_fresh", "file.write", End.AddMinutes(-10), null)];
        Evaluate(data).Findings.Should().NotContain(f => f.RuleId == "R4");

        data.PendingApprovals.Add(new PendingApprovalInfo("apr_old", "file.write", End.AddMinutes(-31), null));
        data.PendingApprovals.Add(new PendingApprovalInfo("apr_expired", "deploy.run", End.AddMinutes(-5), End.AddMinutes(-1)));

        var finding = Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R4").Subject;
        finding.Value.Should().Be(2);
        finding.Evidence.Should().Equal("apr_old", "apr_expired");
        finding.Detail.Should().Contain("1 筆已過期");
    }

    [Fact]
    public void R5_PlanDeadlockIsCritical_AndPlanFailureIsWarning()
    {
        var data = QuietWindow();
        data.PlanEvents =
        [
            new AuditEventCount("PLAN_FAILED", 2, "trace_a"),
            new AuditEventCount("PLAN_DEADLOCKED", 1, "trace_b"),
        ];

        var r5 = Evaluate(data).Findings.Where(f => f.RuleId == "R5").ToList();

        r5.Should().HaveCount(2);
        r5.Single(f => f.Subject == "PLAN_DEADLOCKED").Severity.Should().Be(IncidentSeverity.Critical);
        r5.Single(f => f.Subject == "PLAN_FAILED").Severity.Should().Be(IncidentSeverity.Warning);
    }

    [Fact]
    public void R6_AnyServerError_IsReportedPerPath()
    {
        var data = QuietWindow();
        data.HttpErrors = [new HttpStatusCount("/api/v1/plans/{id}", 500, 1)];

        Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R6")
            .Which.Subject.Should().Be("/api/v1/plans/{id}");
    }

    [Fact]
    public void R7_ClientErrorsPerPath_NeedTenAndExcludeAuthFailures()
    {
        var data = QuietWindow();
        data.HttpErrors =
        [
            new HttpStatusCount("/api/v1/user/approvals/{id}/approve", 400, 9),
            new HttpStatusCount("/api/v1/health/score", 401, 30),
        ];
        Evaluate(data).Findings.Should().NotContain(f => f.RuleId == "R7", "9 筆未達門檻，401 歸 R8");

        data.HttpErrors[0] = new HttpStatusCount("/api/v1/user/approvals/{id}/approve", 400, 10);
        var finding = Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R7").Subject;
        finding.Subject.Should().Be("/api/v1/user/approvals/{id}/approve");
        finding.Detail.Should().Contain("400×10");
    }

    [Fact]
    public void R8_AuthFailures_UseAbsoluteThresholdOrSpikeOverHistory()
    {
        var data = QuietWindow();
        data.HttpErrors = [new HttpStatusCount("/api/v1/health/score", 401, 19)];
        Evaluate(data).Findings.Should().NotContain(f => f.RuleId == "R8", "沒有歷史時要 20 筆");

        data.HttpErrors = [new HttpStatusCount("/api/v1/health/score", 401, 20)];
        Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R8");

        // 有歷史、平常是 0：5 筆就算暴增，4 筆不算
        data.HttpErrors = [new HttpStatusCount("/api/v1/admin/x", 403, 5)];
        Evaluate(data, History(("other", 1.0))).Findings.Should().Contain(f => f.RuleId == "R8");
        data.HttpErrors = [new HttpStatusCount("/api/v1/admin/x", 403, 4)];
        Evaluate(data, History(("other", 1.0))).Findings.Should().NotContain(f => f.RuleId == "R8");
    }

    [Theory]
    [InlineData(5, 3, true)]
    [InlineData(10, 3, true)]     // 30%，剛好達標
    [InlineData(20, 3, false)]    // 15%
    [InlineData(4, 2, false)]     // 只有 2 次失敗
    public void R9_LlmFailures_NeedMinimumCountAndRate(int calls, int failures, bool expected)
    {
        var data = QuietWindow();
        data.Llm = new LlmWindowStats(calls, failures, Start.AddHours(-2), false);

        Evaluate(data).Findings.Any(f => f.RuleId == "R9").Should().Be(expected);
    }

    [Fact]
    public void R9_NotesWhenTheInMemoryCounterWasResetInsideTheWindow()
    {
        var data = QuietWindow();
        data.Llm = new LlmWindowStats(0, 0, Start.AddMinutes(5), false);

        Evaluate(data).Coverage.Notes.Should().Contain(n => n.Contains("LLM 呼叫統計") && n.Contains("重啟"));
    }

    [Fact]
    public void R10_InteractionErrors_ListIdsButNeverTheErrorText()
    {
        var data = QuietWindow();
        data.InteractionErrors = [new InteractionErrorInfo("hlm_1", "line", Start.AddMinutes(4))];

        var finding = Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R10").Subject;
        finding.Evidence.Should().Equal("hlm_1");
        finding.Detail.Should().Contain("報告不列出內容", "錯誤訊息可能夾帶使用者輸入");
    }

    [Fact]
    public void DroppedHttpErrors_AreDisclosedInTheCoverage()
    {
        var data = QuietWindow();
        data.HttpDroppedErrors = 12;

        Evaluate(data).Coverage.Notes.Should().Contain(n => n.Contains("12 筆錯誤回應沒有記到"));
    }

    [Fact]
    public void R11_NoWorkersIsCritical_DegradedIsWarning_AndDropsAreReported()
    {
        var data = QuietWindow();
        data.Health = new HealthNow(0, HealthScoreService.NoWorkersStatus, 0);
        Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R11")
            .Which.Severity.Should().Be(IncidentSeverity.Critical);

        data.Health = new HealthNow(60, "degraded", 2);
        Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R11")
            .Which.Severity.Should().Be(IncidentSeverity.Warning);

        // worker 崩潰不會留下觀測事件：從數量下降看出來
        data.Health = new HealthNow(100, "healthy", 2);
        data.Snapshots = [new HealthPoint(Start.AddMinutes(1), 100, "healthy", 3)];
        Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R11" && f.Title == "worker 數量下降");

        // 分數掉 30 分以上
        data.Snapshots = [new HealthPoint(Start.AddMinutes(1), 100, "healthy", 2), new HealthPoint(Start.AddMinutes(6), 65, "degraded", 2)];
        Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R11" && f.Title == "健康分數下降");
    }

    [Fact]
    public void R12_StaleSnapshotsAreReported_ButNotRightAfterStartup()
    {
        var data = QuietWindow();
        data.LatestSnapshotAt = End.AddMinutes(-16);   // 超過 3 個 5 分鐘間隔
        Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R12");

        data.LatestSnapshotAt = null;
        Evaluate(data).Findings.Should().ContainSingle(f => f.RuleId == "R12")
            .Which.Detail.Should().Contain("沒有任何健康快照");

        data.HostStartedAt = End.AddMinutes(-10);      // 剛啟動，第一筆快照可能還沒寫
        var result = Evaluate(data);
        result.Findings.Should().NotContain(f => f.RuleId == "R12");
        result.Coverage.RulesNotApplicable.Should().ContainKey("R12");
    }

    [Fact]
    public void Metrics_AreRecordedForTheNextBaseline_AndSourceErrorsBecomeNotes()
    {
        var data = QuietWindow();
        data.Executions = [new CapabilityExecutionStats("file.write", 1, 0, 2, [], [])];
        data.HttpErrors = [new HttpStatusCount("/api/v1/x", 404, 3), new HttpStatusCount("/api/v1/y", 401, 2)];
        data.SourceErrors = ["audit_events（SqliteException）"];

        var result = Evaluate(data);

        result.Metrics.Should().Contain(new KeyValuePair<string, double>("denied:file.write", 2));
        result.Metrics.Should().Contain(new KeyValuePair<string, double>("4xx:/api/v1/x", 3));
        result.Metrics.Should().Contain(new KeyValuePair<string, double>("auth_failures", 2));
        result.Coverage.Notes.Should().Contain(n => n.Contains("audit_events"));
    }
}
