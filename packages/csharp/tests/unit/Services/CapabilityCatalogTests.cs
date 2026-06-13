using BrokerCore;
using BrokerCore.Models;
using BrokerCore.Services;
using Unit.Tests.Helpers;

namespace Unit.Tests.Services;

/// <summary>
/// CapabilityCatalog 行為測試 —— DB 驅動白名單 + 授予/配額生命週期。
///
/// 用 TestDb.CreateInMemory() 建真實 SQLite（已 seed capabilities），
/// 全程確定性輸入，驗證查找/篩選/比對/原子配額消耗的真實行為與邊界。
///
/// 涵蓋：
/// - GetCapability：seed 命中 / 未知回 null
/// - ListCapabilities：無 filter 回全部 / filter 命中 capability_id 或 route / 無命中回空
/// - GetActiveGrant：四元組全中且未過期才回 / 任一不符回 null / 已過期回 null / 非 Active 回 null
/// - CreateGrant：寫入後 Active 且 grant_id 以 grt_ 開頭、可被 GetActiveGrant 取回
/// - ConsumeQuota：無限(-1)恆 true 不遞減 / 有限遞減 / 耗盡轉 Exhausted 且後續 false /
///   未知 grant false / 非 Active false
/// </summary>
public class CapabilityCatalogTests : IDisposable
{
    private readonly global::BrokerCore.Data.BrokerDb _db;  // global:: 避開 Unit.Tests.BrokerCore 命名空間碰撞
    private readonly CapabilityCatalog _sut;

    public CapabilityCatalogTests()
    {
        _db = TestDb.CreateInMemory();
        _sut = new CapabilityCatalog(_db);
    }

    public void Dispose() => _db.Dispose();

    private CapabilityGrant SeedGrant(
        string principalId = "prn_a",
        string taskId = "task_a",
        string sessionId = "ses_a",
        string capabilityId = "file.read",
        int quota = -1,
        GrantStatus status = GrantStatus.Active,
        DateTime? expiresAt = null)
    {
        var grant = new CapabilityGrant
        {
            GrantId = IdGen.New("grt"),
            PrincipalId = principalId,
            TaskId = taskId,
            SessionId = sessionId,
            CapabilityId = capabilityId,
            ScopeOverride = "{}",
            RemainingQuota = quota,
            IssuedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(1),
            Status = status
        };
        _db.Insert(grant);
        return grant;
    }

    // ── GetCapability ──

    [Fact]
    public void GetCapability_SeededId_ReturnsCapability()
    {
        var cap = _sut.GetCapability("file.read");
        cap.Should().NotBeNull();
        cap!.CapabilityId.Should().Be("file.read");
        cap.Route.Should().Be("read_file");
        cap.RiskLevel.Should().Be(RiskLevel.Low);
    }

    [Fact]
    public void GetCapability_UnknownId_ReturnsNull()
    {
        _sut.GetCapability("does.not.exist").Should().BeNull();
    }

    // ── ListCapabilities ──

    [Fact]
    public void ListCapabilities_NoFilter_ReturnsAllSeeded()
    {
        var all = _sut.ListCapabilities();
        all.Should().NotBeEmpty();
        all.Select(c => c.CapabilityId).Should().Contain(new[] { "file.read", "command.execute", "web.search" });
    }

    [Fact]
    public void ListCapabilities_EmptyStringFilter_TreatedAsNoFilter()
    {
        _sut.ListCapabilities("").Should().HaveCount(_sut.ListCapabilities().Count);
    }

    [Fact]
    public void ListCapabilities_FilterMatchesCapabilityId()
    {
        var filtered = _sut.ListCapabilities("memory.");
        filtered.Should().NotBeEmpty();
        filtered.Should().OnlyContain(c => c.CapabilityId.Contains("memory") || c.Route.Contains("memory"));
        filtered.Select(c => c.CapabilityId).Should().Contain("memory.read");
    }

    [Fact]
    public void ListCapabilities_FilterMatchesRoute()
    {
        // route "run_command" 對應 capability_id "command.execute"
        var filtered = _sut.ListCapabilities("run_command");
        filtered.Should().ContainSingle()
            .Which.CapabilityId.Should().Be("command.execute");
    }

    [Fact]
    public void ListCapabilities_NoMatch_ReturnsEmpty()
    {
        _sut.ListCapabilities("zzz_nonexistent_pattern").Should().BeEmpty();
    }

    // ── GetActiveGrant ──

    [Fact]
    public void GetActiveGrant_AllKeysMatchAndNotExpired_ReturnsGrant()
    {
        var seeded = SeedGrant();
        var found = _sut.GetActiveGrant("prn_a", "task_a", "ses_a", "file.read");
        found.Should().NotBeNull();
        found!.GrantId.Should().Be(seeded.GrantId);
    }

    [Theory]
    [InlineData("prn_other", "task_a", "ses_a", "file.read")]
    [InlineData("prn_a", "task_other", "ses_a", "file.read")]
    [InlineData("prn_a", "task_a", "ses_other", "file.read")]
    [InlineData("prn_a", "task_a", "ses_a", "file.write")]
    public void GetActiveGrant_AnyKeyMismatch_ReturnsNull(
        string principalId, string taskId, string sessionId, string capabilityId)
    {
        SeedGrant();
        _sut.GetActiveGrant(principalId, taskId, sessionId, capabilityId).Should().BeNull();
    }

    [Fact]
    public void GetActiveGrant_Expired_ReturnsNull()
    {
        SeedGrant(expiresAt: DateTime.UtcNow.AddHours(-1));
        _sut.GetActiveGrant("prn_a", "task_a", "ses_a", "file.read").Should().BeNull();
    }

    [Theory]
    [InlineData(GrantStatus.Expired)]
    [InlineData(GrantStatus.Revoked)]
    [InlineData(GrantStatus.Exhausted)]
    public void GetActiveGrant_NonActiveStatus_ReturnsNull(GrantStatus status)
    {
        SeedGrant(status: status);
        _sut.GetActiveGrant("prn_a", "task_a", "ses_a", "file.read").Should().BeNull();
    }

    // ── CreateGrant ──

    [Fact]
    public void CreateGrant_PersistsActiveGrant_RetrievableViaGetActiveGrant()
    {
        var expires = DateTime.UtcNow.AddMinutes(30);
        var grant = _sut.CreateGrant(
            taskId: "task_c", sessionId: "ses_c", principalId: "prn_c",
            capabilityId: "file.read", scopeOverride: "{\"x\":1}", quota: 5, expiresAt: expires);

        grant.GrantId.Should().StartWith("grt_");
        grant.Status.Should().Be(GrantStatus.Active);
        grant.RemainingQuota.Should().Be(5);

        var found = _sut.GetActiveGrant("prn_c", "task_c", "ses_c", "file.read");
        found.Should().NotBeNull();
        found!.GrantId.Should().Be(grant.GrantId);
        found.ScopeOverride.Should().Be("{\"x\":1}");
    }

    // ── ConsumeQuota ──

    [Fact]
    public void ConsumeQuota_UnlimitedGrant_ReturnsTrueWithoutDecrementing()
    {
        var grant = SeedGrant(quota: -1);
        _sut.ConsumeQuota(grant.GrantId).Should().BeTrue();

        var reloaded = _db.Get<CapabilityGrant>(grant.GrantId);
        reloaded!.RemainingQuota.Should().Be(-1, "無限配額不應遞減");
        reloaded.Status.Should().Be(GrantStatus.Active);
    }

    [Fact]
    public void ConsumeQuota_FiniteGrant_Decrements()
    {
        var grant = SeedGrant(quota: 3);
        _sut.ConsumeQuota(grant.GrantId).Should().BeTrue();

        var reloaded = _db.Get<CapabilityGrant>(grant.GrantId);
        reloaded!.RemainingQuota.Should().Be(2);
        reloaded.Status.Should().Be(GrantStatus.Active);
    }

    [Fact]
    public void ConsumeQuota_DrainsToZero_TransitionsToExhaustedAndThenRejects()
    {
        var grant = SeedGrant(quota: 2);
        _sut.ConsumeQuota(grant.GrantId).Should().BeTrue();   // 2 -> 1
        _sut.ConsumeQuota(grant.GrantId).Should().BeTrue();   // 1 -> 0, exhausted

        var reloaded = _db.Get<CapabilityGrant>(grant.GrantId);
        reloaded!.RemainingQuota.Should().Be(0);
        reloaded.Status.Should().Be(GrantStatus.Exhausted);

        // 已耗盡 + 狀態非 Active → 後續一律拒絕
        _sut.ConsumeQuota(grant.GrantId).Should().BeFalse();
    }

    [Fact]
    public void ConsumeQuota_UnknownGrant_ReturnsFalse()
    {
        _sut.ConsumeQuota("grt_nonexistent").Should().BeFalse();
    }

    [Fact]
    public void ConsumeQuota_NonActiveGrant_ReturnsFalse()
    {
        var grant = SeedGrant(quota: 5, status: GrantStatus.Revoked);
        _sut.ConsumeQuota(grant.GrantId).Should().BeFalse();

        // 配額不應被動到
        _db.Get<CapabilityGrant>(grant.GrantId)!.RemainingQuota.Should().Be(5);
    }
}
