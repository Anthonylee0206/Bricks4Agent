using Broker.Services;
using Broker.Services.Observability;
using BrokerCore.Models;
using BrokerCore.Services;
using FluentAssertions;
using FunctionPool.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unit.Tests.Broker.Observability;

/// <summary>
/// 收集層對真的 SQLite 做查詢：時間窗外的資料不能算進來、整個時間窗都要算到、
/// 不能讀出使用者對話內容。
/// </summary>
public sealed class AnomalyDigestBuilderTests : IDisposable
{
    private static readonly DateTime Start = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddMinutes(15);

    private readonly TempBrokerDb _temp = TempBrokerDb.Create();
    private readonly HttpStatusMetrics _http = new();
    private readonly LlmProxyMetrics _llm = new();

    public void Dispose() => _temp.Dispose();

    private AnomalyDigestBuilder Builder(bool poolEnabled = false, FakeRegistry? registry = null)
    {
        var services = new ServiceCollection().AddSingleton(_llm);
        if (poolEnabled)
            services.AddSingleton(new HealthScoreService(registry ?? new FakeRegistry(), _temp.Db, containerMgr: null, poolConfig: new PoolConfig()));
        return new AnomalyDigestBuilder(_temp.Db, services.BuildServiceProvider(), _http, NullLogger<AnomalyDigestBuilder>.Instance);
    }

    [Fact]
    public async Task Executions_CountTheWholeWindow_AndNothingOutsideIt()
    {
        // 時間窗內 20 筆成功、5 筆失敗、6 筆被拒（v1 只看最新 60 筆，這裡要全部算到）
        for (var i = 0; i < 20; i++) _temp.AddExecution("file.write", ExecutionState.Succeeded, Start.AddSeconds(10 + i));
        for (var i = 0; i < 5; i++) _temp.AddExecution("file.write", ExecutionState.Failed, Start.AddMinutes(1 + i));
        for (var i = 0; i < 6; i++) _temp.AddExecution("deploy.run", ExecutionState.Denied, Start.AddMinutes(14), "Capability not granted to role");
        _temp.AddExecution("file.write", ExecutionState.Failed, Start.AddSeconds(-1));   // 前一窗
        _temp.AddExecution("file.write", ExecutionState.Failed, End);                    // end 不含

        var data = await Builder().BuildAsync(Start, End, 0, Start.AddHours(-1));

        data.ExecutionsScanned.Should().Be(31);
        var write = data.Executions.Single(e => e.CapabilityId == "file.write");
        (write.Succeeded, write.Failed, write.Denied).Should().Be((20, 5, 0));
        write.SampleRequestIds.Should().HaveCount(5);
        var deploy = data.Executions.Single(e => e.CapabilityId == "deploy.run");
        deploy.Denied.Should().Be(6);
        deploy.TopDenyReasons.Should().ContainSingle().Which.Should().Be("Capability not granted to role（6）");
    }

    [Fact]
    public async Task PendingApprovals_AreCurrentState_NotLimitedByTheWindow()
    {
        var old = _temp.AddApproval("file.write", Start.AddHours(-3));
        _temp.AddApproval("file.write", Start.AddHours(-3), status: ApprovalStatus.Approved);

        var data = await Builder().BuildAsync(Start, End, 0, Start.AddHours(-1));

        data.PendingApprovals.Should().ContainSingle().Which.ApprovalId.Should().Be(old);
        data.PendingApprovals[0].ExpiresAt.Should().BeNull("沒有設定期限時不能當成 0001 年過期");
    }

    [Fact]
    public async Task PlanEvents_AreCountedInTheWindow_AndTheFloorSkipsAlreadyScannedEvents()
    {
        _temp.AddAudit("PLAN_FAILED", Start.AddMinutes(1));
        var floor = _temp.MaxAuditEventId();
        _temp.AddAudit("PLAN_FAILED", Start.AddMinutes(2));
        _temp.AddAudit("PLAN_DEADLOCKED", Start.AddMinutes(3));
        _temp.AddAudit("API_REQUEST", Start.AddMinutes(4), "/api/v1/health/score");
        _temp.AddAudit("PLAN_FAILED", End.AddMinutes(1));   // 時間窗之外

        var all = await Builder().BuildAsync(Start, End, 0, Start.AddHours(-1));
        all.PlanEvents.Single(p => p.EventType == "PLAN_FAILED").Count.Should().Be(2);
        all.PlanEvents.Single(p => p.EventType == "PLAN_DEADLOCKED").Count.Should().Be(1);
        all.AuditScanned.Should().Be(4);

        var afterFloor = await Builder().BuildAsync(Start, End, floor, Start.AddHours(-1));
        afterFloor.PlanEvents.Single(p => p.EventType == "PLAN_FAILED").Count.Should().Be(1);
        afterFloor.AuditScanned.Should().Be(3);
        afterFloor.AuditMaxEventId.Should().BeGreaterThan(floor);
    }

    [Fact]
    public async Task InteractionErrors_KeepOnlyIds_NeverTheConversationOrTheErrorText()
    {
        // 錯誤訊息本身也可能夾帶使用者輸入（例如「專案名稱「…」已存在」）
        _temp.AddInteraction(Start.AddMinutes(2), "專案名稱「王小明的身分證 A123456789」已存在", rawInput: "我的身分證字號是 A123456789");
        _temp.AddInteraction(Start.AddMinutes(3), error: null);
        _temp.AddInteraction(End.AddMinutes(2), "outside the window");

        var data = await Builder().BuildAsync(Start, End, 0, Start.AddHours(-1));

        data.InteractionsScanned.Should().Be(2);
        var error = data.InteractionErrors.Should().ContainSingle().Subject;
        error.Channel.Should().Be("line");
        error.ToString().Should().NotContain("A123456789").And.NotContain("王小明");
    }

    [Fact]
    public async Task LlmCalls_ExcludeTheReportsOwnNarration()
    {
        var at = Start.AddMinutes(1);
        _llm.Record(new LlmCallRecord { Ts = at, TaskType = "high_level", Success = false });
        _llm.Record(new LlmCallRecord { Ts = at, TaskType = "high_level", Success = true });
        _llm.Record(new LlmCallRecord { Ts = at, TaskType = IncidentNarrator.TaskType, Success = false });
        _llm.Record(new LlmCallRecord { Ts = End.AddMinutes(1), TaskType = "high_level", Success = false });

        var data = await Builder().BuildAsync(Start, End, 0, Start.AddHours(-1));

        data.Llm.Should().NotBeNull();
        (data.Llm!.Calls, data.Llm.Failures).Should().Be((2, 1));
    }

    [Fact]
    public async Task HttpErrors_ComeFromTheOutermostCounter()
    {
        _http.Record("/api/v1/health/score", 401, Start.AddMinutes(1));
        _http.Record("/api/v1/health/score", 200, Start.AddMinutes(1));

        var data = await Builder().BuildAsync(Start, End, 0, Start.AddHours(-1));

        data.HttpMetricsAvailable.Should().BeTrue();
        data.HttpErrors.Should().ContainSingle().Which.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task PoolDisabled_IsDetectedFromTheMissingHealthService()
    {
        var data = await Builder(poolEnabled: false).BuildAsync(Start, End, 0, Start.AddHours(-1));

        data.PoolEnabled.Should().BeFalse();
        data.Health.Should().BeNull();
    }

    [Fact]
    public async Task PoolEnabled_ReadsHeartbeatLossSnapshotsAndCurrentHealth()
    {
        _temp.AddHeartbeatLost("wkr_a", Start.AddMinutes(5));
        _temp.AddHeartbeatLost("wkr_b", Start.AddMinutes(-5));   // 前一窗
        _temp.AddSnapshot(Start.AddMinutes(1), 100, "healthy", 2);
        _temp.AddSnapshot(Start.AddMinutes(6), 40, "critical", 1);

        var data = await Builder(poolEnabled: true, new FakeRegistry().Add("wkr_c", "file.write")).BuildAsync(Start, End, 0, Start.AddHours(-1));

        data.PoolEnabled.Should().BeTrue();
        data.HeartbeatLost.Should().ContainSingle().Which.WorkerId.Should().Be("wkr_a");
        data.Snapshots.Select(s => s.Score).Should().Equal(100, 40);
        data.LatestSnapshotAt.Should().Be(Start.AddMinutes(6));
        data.Health.Should().NotBeNull();
        data.Health!.WorkerCount.Should().Be(1);
        data.SnapshotInterval.Should().Be(HealthScoreSnapshotService.TickInterval);
    }
}
