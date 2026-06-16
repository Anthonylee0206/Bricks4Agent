using Broker.Models;
using Broker.Services;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unit.Tests.Helpers;

namespace Unit.Tests.Services;

/// <summary>
/// I1 — MultiSigApprovalService 行為測試。
///
/// 重點不變式：
/// - 沒 rule 或 min_approvers ≤ 1 → 等同單人決策（pass-through 給 inner）
/// - 有 rule 且 min=2：
///     * 第一個 admin Approve → record 寫入、inner 沒被 Approve（status 仍 pending）
///     * 第二個不同 admin Approve → 達門檻、inner 被 Approve
///     * 同一個 admin Approve 兩次 → idempotent、不算第二票
/// - Reject：任一 admin Reject → 立刻 inner.Reject、不論已多少 approve
/// </summary>
public class MultiSigApprovalServiceTests : IDisposable
{
    private readonly BrokerDb _db;
    public MultiSigApprovalServiceTests()
    {
        _db = TestDb.CreateInMemory();
        // EnsureTable 我加的兩張 — TestDb.CreateInMemory 只跑 BrokerDbInitializer
        _db.EnsureTable<MultiSigRule>();
        _db.EnsureTable<ApprovalDecisionRecord>();
    }
    public void Dispose() => _db.Dispose();

    private MultiSigApprovalService NewService(ITradingApprovalService inner)
        => new(inner, _db, Substitute.For<IAuditService>(), new NullLogger<MultiSigApprovalService>());

    private static ITradingApprovalService FakeInner(TradingApprovalRequest record, out List<(string Aid, string By, string? Reason, string Action)> calls)
    {
        var captured = new List<(string, string, string?, string)>();
        var mock = Substitute.For<ITradingApprovalService>();
        mock.Get(record.ApprovalId).Returns(record);
        mock.Approve(record.ApprovalId, Arg.Any<string>(), Arg.Any<string?>())
            .Returns(ci => { captured.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<string?>(2), "approve")); return true; });
        mock.Reject(record.ApprovalId, Arg.Any<string>(), Arg.Any<string?>())
            .Returns(ci => { captured.Add((ci.ArgAt<string>(0), ci.ArgAt<string>(1), ci.ArgAt<string?>(2), "reject")); return true; });
        calls = captured;
        return mock;
    }

    private static TradingApprovalRequest NewRecord(string cap = "trading.order")
        => new()
        {
            ApprovalId = "apr_test_" + Guid.NewGuid().ToString("N")[..8],
            CapabilityId = cap,
            TraceId = "trc_test",
            Status = "pending",
            RequestedAt = DateTime.UtcNow,
        };

    [Fact]
    public void NoRule_PassesThroughToInner()
    {
        var rec = NewRecord("strategy.signal");
        var inner = FakeInner(rec, out var calls);
        var svc = NewService(inner);

        svc.Approve(rec.ApprovalId, "admin1", "ok").Should().BeTrue();
        calls.Should().ContainSingle(c => c.Action == "approve" && c.By == "admin1");
    }

    [Fact]
    public void RuleMinOne_PassesThrough()
    {
        var rec = NewRecord("trading.order");
        _db.Insert(new MultiSigRule { CapabilityId = "trading.order", MinApprovers = 1, Enabled = true });

        var inner = FakeInner(rec, out var calls);
        var svc = NewService(inner);

        svc.Approve(rec.ApprovalId, "admin1", "ok").Should().BeTrue();
        calls.Should().ContainSingle(c => c.Action == "approve");
    }

    [Fact]
    public void MinTwo_FirstApproverHoldsPending_NoInnerApprove()
    {
        var rec = NewRecord("trading.order");
        _db.Insert(new MultiSigRule { CapabilityId = "trading.order", MinApprovers = 2, Enabled = true });

        var inner = FakeInner(rec, out var calls);
        var svc = NewService(inner);

        svc.Approve(rec.ApprovalId, "admin1", "first sig").Should().BeTrue();
        calls.Where(c => c.Action == "approve").Should().BeEmpty("第一票不該真 approve");

        var decisions = _db.Query<ApprovalDecisionRecord>(
            "SELECT * FROM approval_decisions WHERE approval_id = @aid", new { aid = rec.ApprovalId });
        decisions.Should().ContainSingle()
            .Which.ApproverPid.Should().Be("admin1");
    }

    [Fact]
    public void MinTwo_SecondDifferentApproverReachesThreshold()
    {
        var rec = NewRecord("trading.order");
        _db.Insert(new MultiSigRule { CapabilityId = "trading.order", MinApprovers = 2, Enabled = true });

        var inner = FakeInner(rec, out var calls);
        var svc = NewService(inner);

        svc.Approve(rec.ApprovalId, "admin1", "first").Should().BeTrue();
        svc.Approve(rec.ApprovalId, "admin2", "second").Should().BeTrue();
        calls.Where(c => c.Action == "approve").Should().HaveCount(1, "達門檻才呼 inner.Approve");
    }

    [Fact]
    public void SameApproverTwice_DoesNotDoubleCount()
    {
        var rec = NewRecord("trading.order");
        _db.Insert(new MultiSigRule { CapabilityId = "trading.order", MinApprovers = 2, Enabled = true });

        var inner = FakeInner(rec, out var calls);
        var svc = NewService(inner);

        svc.Approve(rec.ApprovalId, "admin1", "first");
        svc.Approve(rec.ApprovalId, "admin1", "again");   // 同人重按
        calls.Where(c => c.Action == "approve").Should().BeEmpty("同 admin 重按不算第二票");

        var decisions = _db.Query<ApprovalDecisionRecord>(
            "SELECT * FROM approval_decisions WHERE approval_id = @aid", new { aid = rec.ApprovalId });
        decisions.Should().ContainSingle("重複的 approve 不該寫第二筆");
    }

    [Fact]
    public void MinThree_NeedsThreeDifferentApprovers()
    {
        var rec = NewRecord("trading.order");
        _db.Insert(new MultiSigRule { CapabilityId = "trading.order", MinApprovers = 3, Enabled = true });

        var inner = FakeInner(rec, out var calls);
        var svc = NewService(inner);

        svc.Approve(rec.ApprovalId, "a1");
        svc.Approve(rec.ApprovalId, "a2");
        calls.Where(c => c.Action == "approve").Should().BeEmpty();

        svc.Approve(rec.ApprovalId, "a3");
        calls.Where(c => c.Action == "approve").Should().ContainSingle();
    }

    [Fact]
    public void Reject_ImmediatelyTerminates_RegardlessOfPriorApprovals()
    {
        var rec = NewRecord("trading.order");
        _db.Insert(new MultiSigRule { CapabilityId = "trading.order", MinApprovers = 3, Enabled = true });

        var inner = FakeInner(rec, out var calls);
        var svc = NewService(inner);

        svc.Approve(rec.ApprovalId, "a1");
        svc.Approve(rec.ApprovalId, "a2");
        svc.Reject(rec.ApprovalId, "a3", "spotted attack").Should().BeTrue();

        calls.Should().ContainSingle(c => c.Action == "reject");
        calls.Where(c => c.Action == "approve").Should().BeEmpty();
    }

    [Fact]
    public void DisabledRule_PassesThrough()
    {
        var rec = NewRecord("trading.order");
        _db.Insert(new MultiSigRule { CapabilityId = "trading.order", MinApprovers = 5, Enabled = false });

        var inner = FakeInner(rec, out var calls);
        var svc = NewService(inner);

        svc.Approve(rec.ApprovalId, "admin1").Should().BeTrue();
        calls.Where(c => c.Action == "approve").Should().ContainSingle("rule disabled = pass-through");
    }
}
