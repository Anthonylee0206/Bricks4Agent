using System.Net;
using System.Text.Json;
using Broker.Services.Observability;
using BrokerCore;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;
using FunctionPool.Registry;
using Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WorkerSdk;
using Xunit.Abstractions;

namespace Integration.Tests.Api;

/// <summary>
/// 異常偵測報告的故障注入評估：自己製造故障當「標準答案」，檢查規則有沒有抓到（召回），
/// 以及正常流量時會不會亂報（誤報，C0 對照組）。
///
/// F1、F2、F6、F7、F8 走真實流程（TCP worker、加密 HTTP、中介層、LLM 代理）；
/// F3、F4、F5 直接在服務層建立狀態（真的派發失敗、審批逾時、計畫死結需要大量前置設定，
/// 這裡驗的是偵測，不是那些流程本身）。
/// 每個測試只看自己的時間窗，彼此不互相影響；留下的狀態（例如待審批）測試結束前會清掉。
/// </summary>
public sealed class IncidentReportFaultInjectionTests : IClassFixture<IncidentReportFixture>
{
    private const string AdminRole = "role_admin";
    private const string ReaderRole = "role_reader";

    private readonly IncidentReportFixture _fixture;
    private readonly EncryptedBrokerClient _client;
    private readonly ITestOutputHelper _out;

    public IncidentReportFaultInjectionTests(IncidentReportFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _client = new EncryptedBrokerClient(fixture.Client, fixture.Services);
        _out = output;
    }

    private IncidentReportService Reports => _fixture.Services.GetRequiredService<IncidentReportService>();
    private BrokerDb Db => _fixture.Services.GetRequiredService<BrokerDb>();

    // ── C0 對照組：正常流量不應觸發任何規則 ──
    [Fact]
    public async Task C0_NormalTraffic_ProducesNoFindings()
    {
        await using var worker = await StartWorkerAsync(heartbeatSeconds: 1);
        var admin = _client.OpenSession(AdminRole);
        var start = DateTime.UtcNow;

        for (var i = 0; i < 10; i++)
        {
            var reply = await _client.SendPlainAsync("GET", "/api/v1/health/score", bearerToken: admin.ScopedToken);
            reply.StatusCode.Should().Be(HttpStatusCode.OK, "the response was {0}", reply);
        }

        var report = await RunReportAsync(start);

        report.Findings.Should().BeEmpty("正常流量不應該觸發任何規則");
        report.Coverage.Scanned["http_responses"].Should().BeGreaterThanOrEqualTo(10);
        report.Coverage.RulesRun.Should().Contain(["R1", "R2", "R3", "R6", "R7", "R8", "R9", "R11"]);
    }

    // ── F1：worker 卡住（連線還在，但不再送心跳）──
    [Fact]
    public async Task F1_HungWorker_IsDetectedAsHeartbeatLoss()
    {
        var start = DateTime.UtcNow;
        await using var worker = await StartWorkerAsync(heartbeatSeconds: 3600);

        await WaitUntilAsync(() => Db.Query<ObservationEvent>(
                "SELECT * FROM observation_events WHERE event_type = 'WORKER_HEARTBEAT_LOST' AND worker_id = @id",
                new { id = worker.WorkerId }).Count > 0,
            TimeSpan.FromSeconds(20), "the heartbeat monitor should drop the silent worker");

        var report = await RunReportAsync(start);

        report.Findings.Should().Contain(f => f.RuleId == "R1" && f.Subject == worker.WorkerId);
        report.Findings.Should().Contain(f => f.RuleId == "R11", "worker 被移除後已經沒有 worker 在線");
    }

    // ── F2：沒有權限的請求連續被政策拒絕（真的經過 PEP）──
    [Fact]
    public async Task F2_PolicyDenialBurst_IsDetected()
    {
        var admin = _client.OpenSession(AdminRole);
        var start = DateTime.UtcNow;

        for (var i = 0; i < 6; i++)
        {
            var reply = await _client.SendEncryptedAsync(admin, "/api/v1/execution-requests/submit", new
            {
                capability_id = "incident.test.not_in_catalog",
                idempotency_key = Guid.NewGuid().ToString("N"),
                intent = "fault injection F2",
                payload = new { },
            }, admin.ScopedToken);
            reply.Body.Should().Contain("Denied", "the request should reach the PEP and be denied: {0}", reply);
        }

        var report = await RunReportAsync(start);

        var finding = report.Findings.Should().ContainSingle(f => f.RuleId == "R3").Subject;
        finding.Subject.Should().Be("incident.test.not_in_catalog");
        finding.Value.Should().Be(6);
        finding.Detail.Should().Contain("not found in catalog");
    }

    // ── F3：某個能力的執行大量失敗（服務層建立狀態）──
    [Fact]
    public async Task F3_ExecutionFailures_AreDetected()
    {
        var start = DateTime.UtcNow;
        for (var i = 0; i < 6; i++)
            SeedExecution("incident.test.flaky", i < 4 ? ExecutionState.Failed : ExecutionState.Succeeded);

        var report = await RunReportAsync(start);

        var finding = report.Findings.Should().ContainSingle(f => f.RuleId == "R2").Subject;
        finding.Subject.Should().Be("incident.test.flaky");
        finding.Severity.Should().Be(IncidentSeverity.Critical, "4/6 的失敗率超過 50%");
    }

    // ── F4：審批一直沒人處理（服務層建立狀態，結束時清掉）──
    [Fact]
    public async Task F4_StuckApprovals_AreDetected()
    {
        var now = DateTime.UtcNow;
        var ids = new[]
        {
            SeedApproval(createdAt: now.AddMinutes(-40), expiresAt: null),
            SeedApproval(createdAt: now.AddMinutes(-5), expiresAt: now.AddMinutes(-1)),
        };

        try
        {
            var report = await RunReportAsync(now);

            var finding = report.Findings.Should().ContainSingle(f => f.RuleId == "R4").Subject;
            finding.Value.Should().Be(2);
            finding.Evidence.Should().BeEquivalentTo(ids);
        }
        finally
        {
            Db.Execute("UPDATE approval_requests SET status = @expired WHERE approval_id IN (@a, @b)",
                new { expired = (int)ApprovalStatus.Expired, a = ids[0], b = ids[1] });
        }
    }

    // ── F5：計畫失敗與死結（服務層寫稽核事件）──
    [Fact]
    public async Task F5_PlanFailureAndDeadlock_AreDetected()
    {
        var start = DateTime.UtcNow;
        var audit = _fixture.Services.GetRequiredService<IAuditService>();
        audit.RecordEvent(IdGen.New("trace"), "PLAN_FAILED", resourceRef: IdGen.New("plan"));
        audit.RecordEvent(IdGen.New("trace"), "PLAN_DEADLOCKED", resourceRef: IdGen.New("plan"));

        var report = await RunReportAsync(start);

        report.Findings.Should().Contain(f => f.RuleId == "R5" && f.Subject == "PLAN_FAILED");
        report.Findings.Should().Contain(f => f.RuleId == "R5" && f.Subject == "PLAN_DEADLOCKED" && f.Severity == IncidentSeverity.Critical);
    }

    // ── F6：錯誤 token 連續呼叫（被驗證中介層擋下，稽核看不到）──
    [Fact]
    public async Task F6_AuthFailureBurst_IsDetected_EvenThoughAuditNeverSeesIt()
    {
        var start = DateTime.UtcNow;
        for (var i = 0; i < 25; i++)
        {
            var reply = await _client.SendPlainAsync("GET", "/api/v1/health/score", bearerToken: "not-a-real-token");
            reply.Status.Should().BeOneOf(new[] { 401, 403 }, "the response was {0}", reply);
        }

        var report = await RunReportAsync(start);

        report.Findings.Should().ContainSingle(f => f.RuleId == "R8").Which.Value.Should().Be(25);

        // 稽核中介層排在驗證之後：這 25 筆在 audit_events 裡一筆都沒有，只有最外層的計數器看得到
        var audited = Db.Query<CountRow>(
            "SELECT COUNT(*) AS Cnt FROM audit_events WHERE event_type = 'API_RESPONSE' AND resource_ref = '/api/v1/health/score' AND occurred_at >= @start",
            new { start }).Single().Cnt;
        audited.Should().Be(0);
    }

    // ── F7：使用者審批頁按「核准」（main 現有的 bug：被加密層擋成 400）──
    [Fact]
    public async Task F7_ApprovalPageButtonErrors_AreDetectedPerEndpoint()
    {
        var start = DateTime.UtcNow;
        for (var i = 0; i < 12; i++)
        {
            var reply = await _client.SendPlainAsync("POST", $"/api/v1/user/approvals/{IdGen.New("apr")}/approve",
                "{\"token\":\"not-a-valid-link\",\"reason\":\"F7\"}");
            reply.Status.Should().Be(400, "the response was {0}", reply);
        }

        var report = await RunReportAsync(start);

        var finding = report.Findings.Should().ContainSingle(f => f.RuleId == "R7").Subject;
        finding.Subject.Should().Be("/api/v1/user/approvals/{approvalId}/approve", "不同的審批 ID 要歸成同一個端點（路由範本）");
        finding.Value.Should().Be(12);
    }

    // ── F9：亂打不存在的網址（例如掃描）：全部落在同一組，計數器不會被灌爆 ──
    [Fact]
    public async Task F9_RandomUnknownPaths_LandInOneBucket()
    {
        var start = DateTime.UtcNow;
        for (var i = 0; i < 15; i++)
        {
            var reply = await _client.SendPlainAsync("GET", $"/api/v1/scan-{Guid.NewGuid():N}");
            reply.Status.Should().Be(404, "the response was {0}", reply);
        }

        var report = await RunReportAsync(start);

        var finding = report.Findings.Should().ContainSingle(f => f.RuleId == "R7").Subject;
        finding.Subject.Should().Be(HttpStatusMetrics.UnmatchedRoute);
        finding.Value.Should().Be(15);
    }

    // ── F8：LLM 端點掛掉；報告本身照樣產生，只是少了敘述 ──
    [Fact]
    public async Task F8_LlmOutage_IsDetected_AndTheReportSurvivesTheNarratorFailing()
    {
        var start = DateTime.UtcNow;
        var llm = _fixture.Services.GetRequiredService<ILlmProxyService>();
        var body = JsonSerializer.SerializeToElement(new { messages = new[] { new { role = "user", content = "ping" } } });
        for (var i = 0; i < 4; i++)
        {
            var call = () => llm.ChatAsync(body);
            await call.Should().ThrowAsync<Exception>("LlmProxy points at a closed port in this fixture");
        }

        var options = _fixture.Services.GetRequiredService<IncidentReportOptions>();
        options.Narrate = true;
        try
        {
            var report = await RunReportAsync(start);

            report.Findings.Should().ContainSingle(f => f.RuleId == "R9").Which.Value.Should().Be(4,
                "報告自己的敘述呼叫也會失敗，但不能算進 R9");
            report.NarrativeStatus.Should().Be("failed");
            _fixture.Services.GetRequiredService<IncidentReportStore>().Get(report.ReportId)!
                .Findings.Should().Contain(f => f.RuleId == "R9", "LLM 失敗不能讓規則結果遺失");
        }
        finally
        {
            options.Narrate = false;
        }
    }

    // ── API：只有管理員能看、能手動觸發 ──
    [Fact]
    public async Task Api_AdminCanRunListAndRead_ReaderIsRejected()
    {
        var admin = _client.OpenSession(AdminRole);

        var run = await _client.SendEncryptedAsync(admin, "/api/v1/observability/incident-reports/run", new { minutes = 1 }, admin.ScopedToken);
        run.StatusCode.Should().Be(HttpStatusCode.OK, "the response was {0}", run);
        using var runDoc = JsonDocument.Parse(run.Body);
        var reportId = runDoc.RootElement.GetProperty("data").GetProperty("report_id").GetString();
        reportId.Should().StartWith("incident_");
        runDoc.RootElement.GetProperty("data").GetProperty("blind_spots").GetArrayLength().Should().BeGreaterThan(0);

        var list = await _client.SendPlainAsync("GET", "/api/v1/observability/incident-reports?limit=50", bearerToken: admin.ScopedToken);
        list.StatusCode.Should().Be(HttpStatusCode.OK, "the response was {0}", list);
        list.Body.Should().Contain(reportId!);

        var detail = await _client.SendPlainAsync("GET", $"/api/v1/observability/incident-reports/{reportId}", bearerToken: admin.ScopedToken);
        detail.StatusCode.Should().Be(HttpStatusCode.OK, "the response was {0}", detail);
        var missing = await _client.SendPlainAsync("GET", "/api/v1/observability/incident-reports/incident_missing", bearerToken: admin.ScopedToken);
        missing.Status.Should().Be(404);

        var reader = _client.OpenSession(ReaderRole);
        var denied = await _client.SendPlainAsync("GET", "/api/v1/observability/incident-reports", bearerToken: reader.ScopedToken);
        denied.Status.Should().BeOneOf(new[] { 401, 403 }, "the response was {0}", denied);
    }

    // ── helpers ──

    private async Task<IncidentReport> RunReportAsync(DateTime start)
    {
        await Task.Delay(1100);   // HTTP 計數以秒分桶；讓最後一秒的請求落在時間窗內
        var report = await Reports.RunAsync(start, DateTime.UtcNow, IncidentReportTriggers.Manual);
        _out.WriteLine($"report {report.ReportId}: {report.Findings.Count} finding(s), max={report.MaxSeverity}, {report.DurationMs}ms");
        foreach (var f in report.Findings)
            _out.WriteLine($"  {f.RuleId} [{f.Severity}] {f.Subject} = {f.Value} — {f.Detail}");
        foreach (var (rule, reason) in report.Coverage.RulesNotApplicable)
            _out.WriteLine($"  (n/a) {rule}: {reason}");
        return report;
    }

    private void SeedExecution(string capabilityId, ExecutionState state)
    {
        var now = DateTime.UtcNow;
        Db.Insert(new ExecutionRequest
        {
            RequestId = IdGen.New("req"),
            TaskId = "task_incident_f3",
            SessionId = "ses_incident_f3",
            PrincipalId = "prn_incident_f3",
            CapabilityId = capabilityId,
            ExecutionStateValue = (int)state,
            TraceId = IdGen.New("trace"),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private string SeedApproval(DateTime createdAt, DateTime? expiresAt)
    {
        var approval = new ApprovalRequest
        {
            ApprovalId = IdGen.New("apr"),
            RequestId = IdGen.New("req"),
            TaskId = "task_incident_f4",
            SessionId = "ses_incident_f4",
            PrincipalId = "prn_incident_f4",
            CapabilityId = "incident.test.needs_approval",
            StatusValue = (int)ApprovalStatus.Pending,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt ?? default,
            TraceId = IdGen.New("trace"),
        };
        Db.Insert(approval);
        return approval.ApprovalId;
    }

    private async Task<RunningWorker> StartWorkerAsync(int heartbeatSeconds)
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var workerId = $"wkr_incident_{Guid.NewGuid():N}"[..24];
        var host = new WorkerHost(new WorkerHostOptions
        {
            WorkerId = workerId,
            BrokerHost = "127.0.0.1",
            BrokerPort = _fixture.PoolPort,
            WorkerType = "incident-test",
            HeartbeatIntervalSeconds = heartbeatSeconds,
            AutoReconnect = false,
            // 不強制 worker 簽章時，broker 仍要求註冊訊息帶齊認證欄位（WorkerSession 的既有行為）
            WorkerAuthKeyId = "incident-test-key",
            WorkerAuthSharedSecret = "incident-test-secret",
        }, NullLogger<WorkerHost>.Instance);
        host.RegisterHandler(new NoopHandler());
        var worker = new RunningWorker(workerId, host, host.RunAsync(cts.Token), cts);

        var registry = _fixture.Services.GetRequiredService<IWorkerRegistry>();
        await WaitUntilAsync(() => registry.GetAllWorkers().Any(w => w.WorkerId == worker.WorkerId),
            TimeSpan.FromSeconds(15), "the worker should register");
        return worker;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException($"Timed out after {timeout.TotalSeconds}s: {because}");
            await Task.Delay(200);
        }
    }

    private sealed class RunningWorker(string workerId, WorkerHost host, Task run, CancellationTokenSource cts) : IAsyncDisposable
    {
        public string WorkerId => workerId;

        public async ValueTask DisposeAsync()
        {
            cts.Cancel();
            host.Stop();
            try { await run; } catch (OperationCanceledException) { }
            cts.Dispose();
        }
    }

    private sealed class NoopHandler : ICapabilityHandler
    {
        public string CapabilityId => "incident.test.noop";

        public Task<(bool Success, string? ResultPayload, string? Error)> ExecuteAsync(
            string requestId, string route, string payload, string scope, CancellationToken ct)
            => Task.FromResult<(bool, string?, string?)>((true, "{}", null));
    }

    private sealed class CountRow
    {
        public int Cnt { get; set; }
    }
}

/// <summary>
/// 故障注入用的主機：心跳逾時縮短成 4 秒（F1 不必等 30 秒；健康分數的「新鮮」門檻是逾時的一半，
/// 4 秒讓 1 秒心跳的正常 worker 有足夠餘裕）、LLM 代理指向關閉的埠（F8），
/// 敘述預設關閉（這組測試驗的是偵測；敘述在單元測試裡驗）。
/// </summary>
public sealed class IncidentReportFixture : FunctionPoolEnabledFixture
{
    public IncidentReportFixture()
        : base(FreeTcpPort(), new Dictionary<string, string?>
        {
            ["FunctionPool:HeartbeatTimeoutSeconds"] = "4",
            ["FunctionPool:HealthCheckIntervalSeconds"] = "1",
            ["Observability:IncidentReport:Narrate"] = "false",
            ["LlmProxy:BaseUrl"] = "http://127.0.0.1:9",
            ["LlmProxy:TimeoutSeconds"] = "3",
        })
    {
    }
}
