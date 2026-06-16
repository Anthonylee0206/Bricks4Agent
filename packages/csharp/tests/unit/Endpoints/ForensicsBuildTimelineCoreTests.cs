using Broker.Endpoints;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;
using Unit.Tests.Helpers;

namespace Broker.Tests.Endpoints;

/// <summary>
/// ForensicsEndpoints.BuildTimelineCore 端對端整合測試。
///
/// 這是 forensics agent 的心臟——合併 audit_events + trading_approval_requests +
/// llm_reasoning_audit 三表、按 ts 排序、依 admin/self scope 過濾。
///
/// 之前只測 BuildLlmPrompts（純函式），這條補上資料聚合層。
/// </summary>
public class ForensicsBuildTimelineCoreTests : IDisposable
{
    private readonly BrokerDb _db;

    public ForensicsBuildTimelineCoreTests()
    {
        _db = TestDb.CreateInMemory();
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void BuildTimelineCore_EmptyDb_ReturnsEmptyTimeline()
    {
        var (events, summary, _) = ForensicsEndpoints.BuildTimelineCore(
            _db,
            since: DateTime.UtcNow.AddHours(-1),
            until: DateTime.UtcNow,
            traceId: null, symbol: null, limit: 100,
            callerPrincipalId: "prn_admin", isAdmin: true);

        events.Should().BeEmpty();
        summary.AuditCount.Should().Be(0);
        summary.ApprovalCount.Should().Be(0);
        summary.LlmCount.Should().Be(0);
    }

    [Fact]
    public void BuildTimelineCore_AuditEventsExist_AppearsInTimeline()
    {
        var svc = new AuditService(_db);
        svc.RecordEvent("trace-001", "API_REQUEST", "prn_test", resourceRef: "/x");
        svc.RecordEvent("trace-001", "API_RESPONSE", "prn_test", resourceRef: "/x");

        var (events, summary, _) = ForensicsEndpoints.BuildTimelineCore(
            _db,
            since: DateTime.UtcNow.AddMinutes(-5),
            until: DateTime.UtcNow.AddMinutes(5),
            traceId: null, symbol: null, limit: 100,
            callerPrincipalId: null, isAdmin: true);

        events.Should().HaveCount(2);
        events.All(e => e.Type == "audit").Should().BeTrue();
        summary.AuditCount.Should().Be(2);
    }

    [Fact]
    public void BuildTimelineCore_NonAdmin_FilteredByOwnPrincipal()
    {
        var svc = new AuditService(_db);
        svc.RecordEvent("trace-a", "X", "prn_alice");
        svc.RecordEvent("trace-b", "X", "prn_bob");

        var (events, _, _) = ForensicsEndpoints.BuildTimelineCore(
            _db,
            since: DateTime.UtcNow.AddMinutes(-5),
            until: DateTime.UtcNow.AddMinutes(5),
            traceId: null, symbol: null, limit: 100,
            callerPrincipalId: "prn_alice", isAdmin: false);

        events.Should().HaveCount(1, "non-admin only sees own principal_id events");
        events[0].PrincipalId.Should().Be("prn_alice");
    }

    [Fact]
    public void BuildTimelineCore_TraceIdFilter_OnlyMatchingTrace()
    {
        var svc = new AuditService(_db);
        svc.RecordEvent("trace-keep", "E", "prn_t");
        svc.RecordEvent("trace-skip", "E", "prn_t");
        svc.RecordEvent("trace-keep", "E", "prn_t");

        var (events, summary, _) = ForensicsEndpoints.BuildTimelineCore(
            _db,
            since: DateTime.UtcNow.AddMinutes(-5),
            until: DateTime.UtcNow.AddMinutes(5),
            traceId: "trace-keep", symbol: null, limit: 100,
            callerPrincipalId: null, isAdmin: true);

        events.Should().HaveCount(2);
        events.All(e => e.TraceId == "trace-keep").Should().BeTrue();
        summary.AuditCount.Should().Be(2);
    }

    [Fact]
    public void BuildTimelineCore_TimeWindow_OnlyIncludesEventsInRange()
    {
        // 在資料庫直接插入「過去 1 天」跟「過去 1 小時」的事件
        var svc = new AuditService(_db);
        svc.RecordEvent("trace-recent", "E", "prn_t");

        // 改一筆的 occurred_at 到 1 天前
        _db.Execute(
            "UPDATE audit_events SET occurred_at = @old WHERE trace_id = 'trace-recent'",
            new { old = DateTime.UtcNow.AddDays(-1).ToString("o") });
        svc.RecordEvent("trace-now", "E", "prn_t");   // 這條是 now

        // 只查過去 1 小時
        var (events, summary, _) = ForensicsEndpoints.BuildTimelineCore(
            _db,
            since: DateTime.UtcNow.AddHours(-1),
            until: DateTime.UtcNow.AddMinutes(5),
            traceId: null, symbol: null, limit: 100,
            callerPrincipalId: null, isAdmin: true);

        events.Should().HaveCount(1, "old event should be filtered out by time window");
        events[0].TraceId.Should().Be("trace-now");
    }

    [Fact]
    public void BuildTimelineCore_ApprovalRequested_AppearsWith3SubEventsIfFull()
    {
        // approval_requested 一筆寫入會在 timeline 產生 1-3 個事件（requested / decided / dispatched）
        _db.Insert(new TradingApprovalRequest
        {
            ApprovalId = "apr-001", TraceId = "trace-apr", CapabilityId = "trading.order",
            Route = "place_order", Payload = "{}",
            PrincipalId = "prn_t", Role = "role_user",
            RequestedAt = DateTime.UtcNow.AddSeconds(-10),
            DecidedAt = DateTime.UtcNow.AddSeconds(-5),
            DecidedBy = "prn_admin",
            DecisionReason = "test approve",
            Status = "approved",
            DispatchedAt = DateTime.UtcNow,
            DispatchedBy = "prn_admin"
        });

        var (events, summary, _) = ForensicsEndpoints.BuildTimelineCore(
            _db,
            since: DateTime.UtcNow.AddMinutes(-5),
            until: DateTime.UtcNow.AddMinutes(5),
            traceId: null, symbol: null, limit: 100,
            callerPrincipalId: null, isAdmin: true);

        // 1 個 approval row → 3 個 timeline events（requested + approved + dispatched）
        events.Should().HaveCount(3);
        events.Select(e => e.Type).Should().BeEquivalentTo(new[]
        {
            "approval_requested", "approval_approved", "approval_dispatched"
        });
        summary.ApprovalCount.Should().Be(1);   // 算原始 row 數、不算展開
    }

    [Fact]
    public void BuildTimelineCore_SortedByTsDescending()
    {
        var svc = new AuditService(_db);
        // 三筆事件、時間 0/1/2 秒前
        svc.RecordEvent("t1", "FIRST", "prn_t");
        Thread.Sleep(50);
        svc.RecordEvent("t2", "SECOND", "prn_t");
        Thread.Sleep(50);
        svc.RecordEvent("t3", "THIRD", "prn_t");

        var (events, _, _) = ForensicsEndpoints.BuildTimelineCore(
            _db,
            since: DateTime.UtcNow.AddMinutes(-5),
            until: DateTime.UtcNow.AddMinutes(5),
            traceId: null, symbol: null, limit: 100,
            callerPrincipalId: null, isAdmin: true);

        events.Should().HaveCount(3);
        events.Should().BeInDescendingOrder(e => e.Ts, "timeline 應該最新在最前");
    }

    [Fact]
    public void BuildTimelineCore_LimitRespected()
    {
        var svc = new AuditService(_db);
        for (int i = 0; i < 20; i++)
            svc.RecordEvent($"trace-{i}", "E", "prn_t");

        var (events, _, _) = ForensicsEndpoints.BuildTimelineCore(
            _db,
            since: DateTime.UtcNow.AddMinutes(-5),
            until: DateTime.UtcNow.AddMinutes(5),
            traceId: null, symbol: null, limit: 5,
            callerPrincipalId: null, isAdmin: true);

        events.Should().HaveCount(5);
    }

    [Fact]
    public void BuildTimelineCore_QueryEchoIncludesScope()
    {
        var (_, _, query) = ForensicsEndpoints.BuildTimelineCore(
            _db,
            since: DateTime.UtcNow.AddHours(-1),
            until: DateTime.UtcNow,
            traceId: null, symbol: null, limit: 50,
            callerPrincipalId: "prn_test", isAdmin: false);

        // anonymous query echo 要含 scope 標示
        var queryJson = System.Text.Json.JsonSerializer.Serialize(query);
        queryJson.Should().Contain("\"scope\":\"self\"");
    }
}
