using Broker.Endpoints;
using Broker.Services;
using BrokerCore.Data;
using BrokerCore.Models;
using FluentAssertions;
using FunctionPool.Container;
using FunctionPool.Models;
using FunctionPool.Network;
using FunctionPool.Registry;

namespace Unit.Tests.Broker;

/// <summary>
/// 健康分數子系統在這個 repo 內的自洽性(2026-10)。
///
/// 這組功能原本是從另一個部署抽出來的,讀的資料在這裡不存在:派發分量讀不到事件、
/// /health/workers 探測的 worker 沒人註冊、沒有 worker 時反而給 100 分。
/// 這些測試鎖住「只用這個 repo 真的會產生的資料,得出不自相矛盾的結論」。
/// </summary>
public class HealthScoreSelfConsistencyTests
{
    private static readonly TimeSpan Timeout30 = TimeSpan.FromSeconds(30);

    // ── 心跳門檻跟著 HeartbeatTimeout 走 ─────────────────────────────────

    [Theory]
    [InlineData(0, "fresh", 100)]
    [InlineData(14, "fresh", 100)]
    [InlineData(15, "stale", 50)]
    [InlineData(29, "stale", 50)]
    [InlineData(30, "lost", 0)]
    public void Heartbeat_ThresholdsDeriveFromTimeout(int ageSeconds, string label, int score)
    {
        var hb = HealthScoreService.ScoreHeartbeat(TimeSpan.FromSeconds(ageSeconds), Timeout30);

        hb.Label.Should().Be(label);
        hb.Score.Should().Be(score);
    }

    [Fact]
    public void Heartbeat_LostIsReachableBeforeMonitorRemovesWorker()
    {
        // 舊版 lost 要 60 秒,但 WorkerHealthMonitor 在 30 秒逾時就移除 worker,
        // 所以 lost 永遠不會被看見。門檻必須不晚於 monitor 的逾時。
        var atTimeout = HealthScoreService.ScoreHeartbeat(Timeout30, Timeout30);

        atTimeout.Label.Should().Be("lost");
    }

    [Fact]
    public void Heartbeat_HonoursConfiguredTimeout()
    {
        var timeout = TimeSpan.FromSeconds(60);

        HealthScoreService.ScoreHeartbeat(TimeSpan.FromSeconds(29), timeout).Label.Should().Be("fresh");
        HealthScoreService.ScoreHeartbeat(TimeSpan.FromSeconds(45), timeout).Label.Should().Be("stale");
        HealthScoreService.ScoreHeartbeat(TimeSpan.FromSeconds(60), timeout).Label.Should().Be("lost");
    }

    // ── 派發分量:依能力加總 ───────────────────────────────────────────

    [Fact]
    public void SumForCapabilities_AddsOnlyTheWorkersCapabilities_AndIgnoresDuplicates()
    {
        var stats = new Dictionary<string, (int succeeded, int failed)>
        {
            ["file.read"] = (8, 1),
            ["file.list"] = (2, 0),
            ["browser.read"] = (50, 50),
        };

        var sum = HealthScoreService.SumForCapabilities(stats, new[] { "file.read", "file.list", "file.read", "file.delete" });

        sum.Should().Be((10, 1));
    }

    [Fact]
    public void ScoreDispatch_NoData_IsNullSoItIsExcludedFromTheWeighting()
    {
        HealthScoreService.ScoreDispatch((0, 0)).Should().BeNull();
    }

    [Fact]
    public void ScoreDispatch_DeclaresCapabilityAttribution()
    {
        var d = HealthScoreService.ScoreDispatch((99, 1))!;

        d.Score.Should().Be(100);
        d.SuccessRatePct.Should().Be(99.0m);
        d.Attribution.Should().Be("capability");
    }

    // ── 整體:用真的 broker.db ─────────────────────────────────────────

    [Fact]
    public async Task Compute_NoWorkers_IsNotHealthy()
    {
        using var temp = CreateDb();
        var svc = new HealthScoreService(new FakeRegistry(), temp.Db);

        var report = await svc.ComputeAsync();

        report.WorkerCount.Should().Be(0);
        report.OverallScore.Should().Be(0);
        report.OverallStatus.Should().Be(HealthScoreService.NoWorkersStatus);
    }

    [Fact]
    public async Task Compute_DispatchComponentComesFromExecutionRequests_AndIgnoresPolicyDenials()
    {
        using var temp = CreateDb();
        var now = DateTime.UtcNow;
        InsertRequest(temp.Db, "file.read", ExecutionState.Succeeded, now);
        InsertRequest(temp.Db, "file.read", ExecutionState.Succeeded, now);
        InsertRequest(temp.Db, "file.read", ExecutionState.Failed, now);
        InsertRequest(temp.Db, "file.read", ExecutionState.Denied, now);              // 政策拒絕,不是 worker 的錯
        InsertRequest(temp.Db, "file.read", ExecutionState.Succeeded, now.AddHours(-1)); // 視窗外
        InsertRequest(temp.Db, "browser.read", ExecutionState.Failed, now);           // 別的 worker 的能力

        var registry = new FakeRegistry().Add("wkr-file", new[] { "file.read" }, WorkerState.Ready, heartbeatAgeSeconds: 1);
        var svc = new HealthScoreService(registry, temp.Db, containerMgr: null, poolConfig: new PoolConfig { HeartbeatTimeout = Timeout30 });

        var report = await svc.ComputeAsync();

        var w = report.Workers.Should().ContainSingle().Subject;
        w.Dispatch.Should().NotBeNull("main 上一定有 execution_requests,派發分量不該永遠空白");
        w.Dispatch!.Succeeded.Should().Be(2);
        w.Dispatch.Failed.Should().Be(1);
        w.Heartbeat!.Label.Should().Be("fresh");
        report.OverallStatus.Should().NotBe(HealthScoreService.NoWorkersStatus);
    }

    // ── 快照:模型與既有部署的表結構一致 ───────────────────────────────

    [Fact]
    public void SnapshotTable_SchemaMatchesExistingDeployments()
    {
        using var temp = CreateDb();

        var sql = temp.Db.Query<SchemaRow>(
            "SELECT sql AS Sql FROM sqlite_master WHERE name = 'health_score_snapshots'").Single().Sql;

        // 這一行取自已部署環境的實際結構;一致代表改模型不需要任何遷移。
        sql.Should().Be("CREATE TABLE \"health_score_snapshots\" (\"snapshot_id\" INTEGER PRIMARY KEY AUTOINCREMENT, " +
                        "\"captured_at\" TEXT, \"overall_score\" INTEGER, \"overall_status\" TEXT, \"worker_count\" INTEGER, " +
                        "\"healthy_count\" INTEGER, \"degraded_count\" INTEGER, \"critical_count\" INTEGER)");
    }

    [Fact]
    public void Snapshot_InsertsAndReadsBack_IncludingNoWorkersStatus()
    {
        using var temp = CreateDb();

        temp.Db.Insert(new HealthScoreSnapshot
        {
            CapturedAt = DateTime.UtcNow,
            OverallScore = 0,
            OverallStatus = HealthScoreService.NoWorkersStatus,
        });

        var rows = temp.Db.Query<HealthScoreSnapshot>("SELECT * FROM health_score_snapshots");
        rows.Should().ContainSingle();
        rows[0].SnapshotId.Should().BePositive();
        rows[0].OverallStatus.Should().Be(HealthScoreService.NoWorkersStatus);
    }

    // ── 資源分量:worker 對到正確的容器 ───────────────────────────────

    private static ContainerStats Stat(string id, string name, double cpu = 10, double mem = 10) =>
        new() { ContainerId = id, ContainerName = name, CpuPercent = cpu, MemoryPercent = mem };

    [Fact]
    public void FindStats_PrefersManagedContainerId_ShortVsFullId()
    {
        const string workerId = "wkr_0123456789abcdef";
        var full = "a1b2c3d4e5f6" + new string('0', 52);
        var stats = new[] { Stat("ffffffffffff", "b4a-file-worker-other"), Stat("a1b2c3d4e5f6", "unrelated-name") };
        var managed = new[] { new ManagedContainer { WorkerId = workerId, ContainerId = full } };

        HealthScoreService.FindStatsForWorker(stats, managed, workerId)!.ContainerId.Should().Be("a1b2c3d4e5f6");
    }

    [Fact]
    public void FindStats_FallsBackToContainerManagerNamingRule()
    {
        // ContainerManager 命名:b4a-{type}-{workerId 前 12 字}
        const string workerId = "wkr_0123456789abcdef";
        var stats = new[] { Stat("aaaaaaaaaaaa", "b4a-file-worker-wkr_01234567"), Stat("bbbbbbbbbbbb", "/b4a-file-worker-wkr_01234567") };

        HealthScoreService.FindStatsForWorker(stats, managed: null, workerId)!.ContainerId.Should().Be("aaaaaaaaaaaa");
    }

    [Fact]
    public void FindStats_OldPrefixHeuristicCaseNowMatches()
    {
        // 舊版取 workerId 第一個 '-' 前那段("wkr_0123456789abcdef",20 字)去比對,
        // 但容器名稱只帶前 12 字,所以永遠對不到 —— 資源分量從來沒有資料。
        const string workerId = "wkr_0123456789abcdef";
        var stats = new[] { Stat("cccccccccccc", "b4a-browser-worker-wkr_01234567") };

        var oldHeuristicMatched = stats.Any(s => s.ContainerName.Contains(workerId.Split('-')[0], StringComparison.OrdinalIgnoreCase));
        oldHeuristicMatched.Should().BeFalse("這是舊版的行為,用來證明原本確實對不到");
        HealthScoreService.FindStatsForWorker(stats, null, workerId).Should().NotBeNull();
    }

    [Fact]
    public void FindStats_DoesNotBorrowAnotherWorkersContainer()
    {
        var stats = new[] { Stat("dddddddddddd", "b4a-file-worker-wkr_zzzzzzzz") };

        HealthScoreService.FindStatsForWorker(stats, null, "wkr_0123456789abcdef").Should().BeNull("對不到就不計,而不是拿別人的數字");
        HealthScoreService.FindStatsForWorker(null, null, "wkr_x").Should().BeNull();
        HealthScoreService.FindStatsForWorker(Array.Empty<ContainerStats>(), null, "wkr_x").Should().BeNull();
    }

    // ── /health/workers:讀 registry,狀態判斷與分數一致 ────────────────

    [Theory]
    [InlineData(WorkerState.Ready, 0, 4, 1, "ready", true)]
    [InlineData(WorkerState.Ready, 4, 4, 1, "busy", false)]
    [InlineData(WorkerState.Busy, 4, 4, 1, "busy", false)]
    [InlineData(WorkerState.Connecting, 0, 4, 1, "connecting", false)]
    [InlineData(WorkerState.Ready, 0, 4, 20, "stale", false)]
    [InlineData(WorkerState.Ready, 0, 4, 31, "lost", false)]
    public void DescribeWorker_StatusRules(WorkerState state, int active, int max, int ageSeconds, string expected, bool available)
    {
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var w = new WorkerInfo
        {
            WorkerId = "wkr-1",
            State = state,
            MaxConcurrent = max,
            LastHeartbeat = now.AddSeconds(-ageSeconds),
        };
        w.ActiveTasks = active;

        var view = HealthCheckEndpoints.DescribeWorker(w, now, Timeout30);

        view.Status.Should().Be(expected);
        view.Available.Should().Be(available);
        view.HeartbeatAgeSeconds.Should().Be(ageSeconds);
    }

    [Fact]
    public void DescribeWorker_AgreesWithHeartbeatScore()
    {
        // 兩個端點對同一個 worker 不能一個說 stale、一個說 fresh。
        var now = DateTime.UtcNow;
        foreach (var age in new[] { 0, 14, 15, 29, 30, 45 })
        {
            var w = new WorkerInfo { WorkerId = "w", State = WorkerState.Ready, LastHeartbeat = now.AddSeconds(-age) };
            var view = HealthCheckEndpoints.DescribeWorker(w, now, Timeout30);
            var hb = HealthScoreService.ScoreHeartbeat(TimeSpan.FromSeconds(age), Timeout30);

            var viewHeartbeatLabel = view.Status is "lost" or "stale" ? view.Status : "fresh";
            viewHeartbeatLabel.Should().Be(hb.Label, $"age={age}s");
        }
    }

    // ── helpers ─────────────────────────────────────────────────────

    private static void InsertRequest(BrokerDb db, string capabilityId, ExecutionState state, DateTime updatedAt)
    {
        db.Insert(new ExecutionRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            TaskId = "task_test",
            SessionId = "ses_test",
            PrincipalId = "prn_test",
            CapabilityId = capabilityId,
            ExecutionStateValue = (int)state,
            TraceId = "trc_test",
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
        });
    }

    private sealed class SchemaRow
    {
        public string Sql { get; set; } = "";
    }

    private static TempBrokerDb CreateDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"broker_health_{Guid.NewGuid():N}.db");
        var db = new BrokerDb($"Data Source={path}");
        new BrokerDbInitializer(db).Initialize();
        return new TempBrokerDb(path, db);
    }

    private sealed class TempBrokerDb : IDisposable
    {
        private readonly string _path;

        public TempBrokerDb(string path, BrokerDb db)
        {
            _path = path;
            Db = db;
        }

        public BrokerDb Db { get; }

        public void Dispose()
        {
            Db.Dispose();
            foreach (var file in new[] { _path, _path + "-shm", _path + "-wal" })
            {
                try
                {
                    if (File.Exists(file))
                        File.Delete(file);
                }
                catch (IOException)
                {
                    // 暫存檔刪不掉不影響測試結果
                }
            }
        }
    }

    /// <summary>只實作健康分數會用到的查詢面;其他成員被呼叫就代表測試假設錯了。</summary>
    private sealed class FakeRegistry : IWorkerRegistry
    {
        private readonly List<WorkerInfo> _workers = new();

        public FakeRegistry Add(string id, string[] capabilities, WorkerState state, int heartbeatAgeSeconds)
        {
            _workers.Add(new WorkerInfo
            {
                WorkerId = id,
                Capabilities = capabilities.ToList(),
                State = state,
                LastHeartbeat = DateTime.UtcNow.AddSeconds(-heartbeatAgeSeconds),
            });
            return this;
        }

        public List<WorkerInfo> GetAllWorkers() => _workers.ToList();
        public List<WorkerInfo> GetWorkersByCapability(string capabilityId) =>
            _workers.Where(w => w.Capabilities.Contains(capabilityId)).ToList();
        public bool HasAvailableWorker(string capabilityId) => GetWorkersByCapability(capabilityId).Any(w => w.IsAvailable);
        public int GetAvailableCount(string capabilityId) => GetWorkersByCapability(capabilityId).Count(w => w.IsAvailable);

        public bool Register(WorkerInfo worker, WorkerConnection connection) => throw new NotSupportedException();
        public bool Deregister(string workerId) => throw new NotSupportedException();
        public WorkerConnection? GetAvailableWorker(string capabilityId) => throw new NotSupportedException();
        public void UpdateHeartbeat(string workerId) => throw new NotSupportedException();
        public void IncrementActiveTask(string workerId) => throw new NotSupportedException();
        public void DecrementActiveTask(string workerId) => throw new NotSupportedException();
        public void SetWorkerState(string workerId, WorkerState state) => throw new NotSupportedException();
    }
}
