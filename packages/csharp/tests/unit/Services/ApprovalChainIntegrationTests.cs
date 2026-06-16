using Broker.Models;
using Broker.Services;
using BrokerCore.Data;
using BrokerCore.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Unit.Tests.Helpers;

namespace Unit.Tests.Services;

/// <summary>
/// 4 層 ITradingApprovalService 裝飾鏈端到端測試。Program.cs 的 DI 順序錯了會在這層抓到。
///
/// 鏈：MultiSigApprovalService → TimeAwareApprovalService → TemplateAwareApprovalService → TradingApprovalService
///
/// 不變式（用真 BrokerDb / 真 ApprovalService、不 mock）：
/// 1. RequiresApproval 走最外層的 TimeAware（時段外強制 require）
/// 2. GetOrCreatePending 走 TemplateAware（命中 template 直接 approved、不入 pending pile）
/// 3. Approve 走 MultiSig（min=2 第一票還 pending、第二票才真 approve）
/// 4. 同 trace_id idempotent（不重複建 pending）
/// </summary>
public class ApprovalChainIntegrationTests : IDisposable
{
    private readonly BrokerDb _db;
    private readonly ITradingApprovalService _chain;
    private readonly TimeAclService _timeAcl;
    private readonly ApprovalTemplateMatcher _matcher;

    public ApprovalChainIntegrationTests()
    {
        _db = TestDb.CreateInMemory();
        _db.EnsureTable<ApprovalTemplate>();
        _db.EnsureTable<TimeAclRule>();
        _db.EnsureTable<MultiSigRule>();
        _db.EnsureTable<ApprovalDecisionRecord>();

        var inner = new TradingApprovalService(_db);
        var audit = Substitute.For<IAuditService>();
        _matcher = new ApprovalTemplateMatcher(_db, new NullLogger<ApprovalTemplateMatcher>());
        _timeAcl = new TimeAclService(_db, new NullLogger<TimeAclService>());

        var templateAware = new TemplateAwareApprovalService(inner, _matcher, audit,
            new NullLogger<TemplateAwareApprovalService>());
        var timeAware = new TimeAwareApprovalService(templateAware, _timeAcl,
            new NullLogger<TimeAwareApprovalService>());
        _chain = new MultiSigApprovalService(timeAware, _db, audit,
            new NullLogger<MultiSigApprovalService>());
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void RequiresApproval_TradingOrder_AlwaysTrue_FromInnerHardcodedList()
    {
        // 原 TradingApprovalService 預設 trading.order ∈ require list、最外層應如實傳遞 true
        _chain.RequiresApproval("trading.order", "place_order").Should().BeTrue();
    }

    [Fact]
    public void RequiresApproval_TradingOrder_ReadOnlyRoutes_Exempt()
    {
        // 修:儀表板 poll list_orders 洗審核。只讀 route 放行、write route 仍受控。
        var inner = new TradingApprovalService(_db);
        inner.RequiresApproval("trading.order", "list_orders").Should().BeFalse();
        inner.RequiresApproval("trading.order", "get_order").Should().BeFalse();
        inner.RequiresApproval("trading.order", "place_order").Should().BeTrue();
        inner.RequiresApproval("trading.order", "cancel_order").Should().BeTrue();
    }

    [Fact]
    public void RequiresApproval_Chain_ListOrders_NotGated()
    {
        // 端到端:整條 decorator chain 也不該再擋 list_orders
        _chain.RequiresApproval("trading.order", "list_orders").Should().BeFalse();
    }

    [Fact]
    public void RequiresApproval_LowRiskCap_NoTimeRule_False()
    {
        _chain.RequiresApproval("strategy.signal", "compute").Should().BeFalse();
    }

    [Fact]
    public void RequiresApproval_LowRiskCap_WithTimeRule_OutsideWindow_ForcedTrue()
    {
        // 加一條 strategy.signal 只在 09-17 UTC auto window
        _db.Insert(new TimeAclRule {
            RuleId = "tacl_test1", CapabilityId = "strategy.signal",
            StartHour = 9, EndHour = 17, WeekdayMask = 0b1111111,
            Timezone = "UTC", Enabled = true,
        });
        // 直接呼叫 IsInsideAutoWindow 確認規則確實寫入 + 條件邏輯通
        var nowSentinel = DateTime.UtcNow;
        var inside = _timeAcl.IsInsideAutoWindow("strategy.signal", nowSentinel);
        inside.Should().NotBeNull("rule 寫入後 IsInsideAutoWindow 不該回 null");

        // 跑一次 Decorator —— inner=false、若時段在窗外應變 true
        var actual = _chain.RequiresApproval("strategy.signal", "compute");
        if (inside == false)
            actual.Should().BeTrue("時段外應強制 require_approval");
        else
            actual.Should().BeFalse("時段內維持原 ACL（false）");
    }

    [Fact]
    public void GetOrCreatePending_NoTemplate_ReturnsPending()
    {
        var traceId = "trc_no_tpl_" + Guid.NewGuid().ToString("N")[..6];
        var rec = _chain.GetOrCreatePending(traceId, "trading.order", "place_order",
            """{"args":{"symbol":"BTC","quantity":0.5}}""", "prn_user", "role_user");
        rec.Status.Should().Be("pending");
    }

    [Fact]
    public void GetOrCreatePending_TemplateMatches_AutoApproved()
    {
        // 加 template：BTC ≤ 0.01 自動放行
        _db.Insert(new ApprovalTemplate {
            TemplateId = "aptpl_btc_small",
            CapabilityId = "trading.order",
            Route = "",
            PayloadMatch = """{"args.symbol":"BTC","args.quantity":{"$lte":0.01}}""",
            MaxUsesPerDay = 0, Enabled = true, Description = "small BTC auto",
            CreatedBy = "test", CreatedAt = DateTime.UtcNow,
        });

        var traceId = "trc_tpl_hit_" + Guid.NewGuid().ToString("N")[..6];
        var rec = _chain.GetOrCreatePending(traceId, "trading.order", "place_order",
            """{"args":{"symbol":"BTC","quantity":0.005}}""", "prn_user", "role_user");

        rec.Status.Should().Be("approved", "命中 template 應立即 approve");
        rec.DecidedBy.Should().StartWith("template:");
    }

    [Fact]
    public void GetOrCreatePending_TemplateMissesByQuantity_StaysPending()
    {
        _db.Insert(new ApprovalTemplate {
            TemplateId = "aptpl_btc_small2",
            CapabilityId = "trading.order",
            PayloadMatch = """{"args.quantity":{"$lte":0.01}}""",
            Enabled = true, CreatedAt = DateTime.UtcNow,
        });

        var traceId = "trc_tpl_miss_" + Guid.NewGuid().ToString("N")[..6];
        var rec = _chain.GetOrCreatePending(traceId, "trading.order", "place_order",
            """{"args":{"symbol":"BTC","quantity":0.5}}""", "prn_user", "role_user");

        rec.Status.Should().Be("pending", "qty 過大不命中 template、應入 pending");
    }

    [Fact]
    public void Approve_WithMultiSigMin2_FirstApproverHoldsPending()
    {
        _db.Insert(new MultiSigRule {
            CapabilityId = "trading.order", MinApprovers = 2, Enabled = true,
            CreatedAt = DateTime.UtcNow,
        });

        var traceId = "trc_msig_" + Guid.NewGuid().ToString("N")[..6];
        var rec = _chain.GetOrCreatePending(traceId, "trading.order", "place_order",
            """{"args":{"symbol":"BTC","quantity":0.5}}""", "prn_user", "role_user");

        _chain.Approve(rec.ApprovalId, "admin1", "first").Should().BeTrue();
        var refetch1 = _chain.Get(rec.ApprovalId);
        refetch1!.Status.Should().Be("pending", "第一票後仍 pending");

        _chain.Approve(rec.ApprovalId, "admin2", "second").Should().BeTrue();
        var refetch2 = _chain.Get(rec.ApprovalId);
        refetch2!.Status.Should().Be("approved", "達 2 票後變 approved");
    }

    [Fact]
    public void GetOrCreatePending_SameTraceId_Idempotent()
    {
        var traceId = "trc_idem_" + Guid.NewGuid().ToString("N")[..6];
        var rec1 = _chain.GetOrCreatePending(traceId, "trading.order", "place_order",
            """{"args":{"symbol":"BTC","quantity":0.5}}""", "prn_u", "role_user");
        var rec2 = _chain.GetOrCreatePending(traceId, "trading.order", "place_order",
            """{"args":{"symbol":"BTC","quantity":0.5}}""", "prn_u", "role_user");
        rec2.ApprovalId.Should().Be(rec1.ApprovalId, "同 trace_id 必須回同一筆");
    }
}
