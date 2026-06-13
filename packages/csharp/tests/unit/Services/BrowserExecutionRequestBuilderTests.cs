using Broker.Services;
using BrokerCore.Models;
using Unit.Tests.Helpers;

namespace Unit.Tests.Services;

/// <summary>
/// BrowserExecutionRequestBuilder.TryBuild 行為測試 —— 從 tool spec + 輸入組瀏覽器執行請求。
///
/// IToolSpecRegistry 用 NSubstitute 替身回傳確定性 ToolSpecView；
/// BrokerDb 用 TestDb.CreateInMemory() 建真實 SQLite（browser_* 表已由 initializer EnsureTable）。
/// 全程確定性輸入，驗證驗證順序、欄位映射、預設值與各邊界拒絕路徑。
///
/// 涵蓋（依 TryBuild 驗證順序）：
/// - tool spec 缺失 / 非 browser / 政策不完整
/// - 必填輸入缺漏（逐欄位 Theory）
/// - max / intended action level 無效 / intended > max 超界
/// - requires_registered_site_binding 但缺 site binding id
/// - site binding：不存在 / 非 active / identity_mode 不符 / site_class 不在白名單 / principal 不符（user_delegated）
/// - user_delegated 缺 user grant；system_account 缺 system binding
/// - user grant：不存在 / 非 active / principal 不符 / site binding 不符 / 過期
/// - system binding：不存在 / 非 active / site binding 不符
/// - session lease：不存在 / 非 active / principal 不符 / identity 不符 / 過期 / site 不符
/// - 成功路徑：欄位從 spec/input 正確映射、Arguments/Scope 空白回 "{}" 預設
/// </summary>
public class BrowserExecutionRequestBuilderTests : IDisposable
{
    private const string ToolId = "browser.demo";

    private readonly global::BrokerCore.Data.BrokerDb _db;  // global:: 避開 Unit.Tests.BrokerCore 命名空間碰撞
    private readonly IToolSpecRegistry _registry;
    private readonly BrowserExecutionRequestBuilder _sut;

    public BrowserExecutionRequestBuilderTests()
    {
        _db = TestDb.CreateInMemory();
        _registry = Substitute.For<IToolSpecRegistry>();
        _sut = new BrowserExecutionRequestBuilder(_registry, _db);
    }

    public void Dispose() => _db.Dispose();

    // ── helpers ──

    private static ToolSpecView BrowserSpec(
        string identityMode = "anonymous",
        string credentialBinding = "none",
        string bindingMode = "ephemeral",
        string reuseScope = "task",
        string siteBindingMode = "public_open",
        string[]? allowedSiteClasses = null,
        bool requiresRegisteredSiteBinding = false,
        string maxActionLevel = "navigate",
        string[]? requiresHumanConfirmationOn = null)
        => new()
        {
            ToolId = ToolId,
            Kind = "browser",
            BrowserProfile = new BrowserToolProfileView { IdentityMode = identityMode },
            BrowserSessionPolicy = new BrowserSessionPolicyView
            {
                BindingMode = bindingMode,
                CredentialBinding = credentialBinding,
                ReuseScope = reuseScope
            },
            BrowserSitePolicy = new BrowserSitePolicyView
            {
                SiteBindingMode = siteBindingMode,
                AllowedSiteClasses = allowedSiteClasses ?? Array.Empty<string>(),
                RequiresRegisteredSiteBinding = requiresRegisteredSiteBinding
            },
            BrowserActionPolicy = new BrowserActionPolicyView
            {
                MaxActionLevel = maxActionLevel,
                RequiresHumanConfirmationOn = requiresHumanConfirmationOn ?? Array.Empty<string>()
            }
        };

    private static BrowserExecutionRequestBuildInput ValidInput()
        => new()
        {
            RequestId = "req_1",
            CapabilityId = "cap_1",
            Route = "browse",
            PrincipalId = "prn_a",
            TaskId = "task_a",
            SessionId = "ses_a",
            StartUrl = "https://example.com",
            IntendedActionLevel = "read",
            ArgumentsJson = "{}",
            ScopeJson = "{}"
        };

    private void Register(string toolId, ToolSpecView? spec) => _registry.Get(toolId).Returns(spec);

    private static void Fails(BrowserExecutionRequestBuildResult result, string expectedError)
    {
        result.Success.Should().BeFalse();
        result.Request.Should().BeNull();
        result.Error.Should().Be(expectedError);
    }

    // ── tool spec gating ──

    [Fact]
    public void TryBuild_ToolSpecMissing_FailsNotFound()
    {
        Register(ToolId, null);
        Fails(_sut.TryBuild(ToolId, ValidInput()), "tool_spec_not_found");
    }

    [Fact]
    public void TryBuild_ToolSpecNotBrowserKind_Fails()
    {
        var spec = BrowserSpec();
        spec.Kind = "function";
        Register(ToolId, spec);
        Fails(_sut.TryBuild(ToolId, ValidInput()), "tool_spec_not_browser");
    }

    [Fact]
    public void TryBuild_BrowserKindCaseInsensitive_PassesKindGate()
    {
        var spec = BrowserSpec();
        spec.Kind = "Browser";
        Register(ToolId, spec);
        _sut.TryBuild(ToolId, ValidInput()).Success.Should().BeTrue();
    }

    [Fact]
    public void TryBuild_MissingProfilePolicy_FailsIncomplete()
    {
        var spec = BrowserSpec();
        spec.BrowserProfile = null;
        Register(ToolId, spec);
        Fails(_sut.TryBuild(ToolId, ValidInput()), "browser_tool_spec_incomplete");
    }

    [Fact]
    public void TryBuild_MissingActionPolicy_FailsIncomplete()
    {
        var spec = BrowserSpec();
        spec.BrowserActionPolicy = null;
        Register(ToolId, spec);
        Fails(_sut.TryBuild(ToolId, ValidInput()), "browser_tool_spec_incomplete");
    }

    // ── required input completeness ──

    public static IEnumerable<object[]> BlankRequiredFieldMutators()
    {
        yield return new object[] { (Action<BrowserExecutionRequestBuildInput>)(i => i.RequestId = "") };
        yield return new object[] { (Action<BrowserExecutionRequestBuildInput>)(i => i.CapabilityId = "") };
        yield return new object[] { (Action<BrowserExecutionRequestBuildInput>)(i => i.Route = "  ") };
        yield return new object[] { (Action<BrowserExecutionRequestBuildInput>)(i => i.PrincipalId = "") };
        yield return new object[] { (Action<BrowserExecutionRequestBuildInput>)(i => i.TaskId = "") };
        yield return new object[] { (Action<BrowserExecutionRequestBuildInput>)(i => i.SessionId = "") };
        yield return new object[] { (Action<BrowserExecutionRequestBuildInput>)(i => i.StartUrl = "") };
        yield return new object[] { (Action<BrowserExecutionRequestBuildInput>)(i => i.IntendedActionLevel = "") };
    }

    [Theory]
    [MemberData(nameof(BlankRequiredFieldMutators))]
    public void TryBuild_AnyRequiredInputBlank_FailsInputIncomplete(Action<BrowserExecutionRequestBuildInput> mutate)
    {
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        mutate(input);
        Fails(_sut.TryBuild(ToolId, input), "browser_request_input_incomplete");
    }

    // ── action level validation ──

    [Fact]
    public void TryBuild_SpecMaxActionLevelUnknown_Fails()
    {
        Register(ToolId, BrowserSpec(maxActionLevel: "teleport"));
        Fails(_sut.TryBuild(ToolId, ValidInput()), "browser_tool_invalid_max_action_level");
    }

    [Fact]
    public void TryBuild_IntendedActionLevelUnknown_Fails()
    {
        Register(ToolId, BrowserSpec(maxActionLevel: "committed_action"));
        var input = ValidInput();
        input.IntendedActionLevel = "teleport";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_invalid_action_level");
    }

    [Fact]
    public void TryBuild_IntendedExceedsMaxActionLevel_Fails()
    {
        // max = navigate(2), intended = committed_action(5)
        Register(ToolId, BrowserSpec(maxActionLevel: "navigate"));
        var input = ValidInput();
        input.IntendedActionLevel = "committed_action";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_action_level_exceeds_policy");
    }

    [Fact]
    public void TryBuild_IntendedEqualsMaxActionLevel_Allowed()
    {
        Register(ToolId, BrowserSpec(maxActionLevel: "read"));
        var input = ValidInput();
        input.IntendedActionLevel = "read";
        _sut.TryBuild(ToolId, input).Success.Should().BeTrue();
    }

    // ── registered site binding requirement ──

    [Fact]
    public void TryBuild_RequiresSiteBindingButMissing_Fails()
    {
        Register(ToolId, BrowserSpec(requiresRegisteredSiteBinding: true));
        Fails(_sut.TryBuild(ToolId, ValidInput()), "browser_request_missing_site_binding");
    }

    // ── site binding lookups ──

    [Fact]
    public void TryBuild_SiteBindingIdNotInDb_Fails()
    {
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        input.SiteBindingId = "sb_missing";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_site_binding_not_found");
    }

    [Fact]
    public void TryBuild_SiteBindingNotActive_Fails()
    {
        SeedSiteBinding("sb_1", status: "revoked");
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        input.SiteBindingId = "sb_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_site_binding_not_found");
    }

    [Fact]
    public void TryBuild_SiteBindingIdentityModeMismatch_Fails()
    {
        SeedSiteBinding("sb_1", identityMode: "system_account");
        Register(ToolId, BrowserSpec(identityMode: "anonymous"));
        var input = ValidInput();
        input.SiteBindingId = "sb_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_site_binding_identity_mismatch");
    }

    [Fact]
    public void TryBuild_SiteBindingClassNotInAllowedList_Fails()
    {
        SeedSiteBinding("sb_1", identityMode: "anonymous", siteClass: "social");
        Register(ToolId, BrowserSpec(identityMode: "anonymous", allowedSiteClasses: new[] { "search", "docs" }));
        var input = ValidInput();
        input.SiteBindingId = "sb_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_site_binding_class_mismatch");
    }

    [Fact]
    public void TryBuild_SiteBindingClassInAllowedList_Allowed()
    {
        SeedSiteBinding("sb_1", identityMode: "anonymous", siteClass: "search");
        Register(ToolId, BrowserSpec(identityMode: "anonymous", allowedSiteClasses: new[] { "search", "docs" }));
        var input = ValidInput();
        input.SiteBindingId = "sb_1";
        var result = _sut.TryBuild(ToolId, input);
        result.Success.Should().BeTrue();
        result.Request!.SiteBindingId.Should().Be("sb_1");
    }

    // ── identity-mode driven requirements ──

    [Fact]
    public void TryBuild_UserDelegatedWithoutUserGrant_Fails()
    {
        Register(ToolId, BrowserSpec(identityMode: "user_delegated"));
        Fails(_sut.TryBuild(ToolId, ValidInput()), "browser_request_missing_user_grant");
    }

    [Fact]
    public void TryBuild_SystemAccountWithoutSystemBinding_Fails()
    {
        Register(ToolId, BrowserSpec(identityMode: "system_account"));
        Fails(_sut.TryBuild(ToolId, ValidInput()), "browser_request_missing_system_binding");
    }

    [Fact]
    public void TryBuild_UserDelegatedSiteBindingPrincipalMismatch_Fails()
    {
        // site binding belongs to a different principal than the request
        SeedSiteBinding("sb_1", identityMode: "user_delegated", principalId: "prn_other");
        SeedUserGrant("ug_1", principalId: "prn_a", siteBindingId: "sb_1");
        Register(ToolId, BrowserSpec(identityMode: "user_delegated"));
        var input = ValidInput();
        input.SiteBindingId = "sb_1";
        input.UserGrantId = "ug_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_site_binding_principal_mismatch");
    }

    // ── user grant lookups ──

    [Fact]
    public void TryBuild_UserGrantNotInDb_Fails()
    {
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        input.UserGrantId = "ug_missing";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_user_grant_not_found");
    }

    [Fact]
    public void TryBuild_UserGrantNotActive_Fails()
    {
        SeedUserGrant("ug_1", principalId: "prn_a", status: "revoked");
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        input.UserGrantId = "ug_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_user_grant_not_found");
    }

    [Fact]
    public void TryBuild_UserGrantPrincipalMismatch_Fails()
    {
        SeedUserGrant("ug_1", principalId: "prn_other");
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        input.UserGrantId = "ug_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_user_grant_principal_mismatch");
    }

    [Fact]
    public void TryBuild_UserGrantSiteBindingMismatch_Fails()
    {
        // grant is scoped to sb_other but request carries sb_1
        SeedSiteBinding("sb_1", identityMode: "anonymous");
        SeedUserGrant("ug_1", principalId: "prn_a", siteBindingId: "sb_other");
        Register(ToolId, BrowserSpec(identityMode: "anonymous"));
        var input = ValidInput();
        input.SiteBindingId = "sb_1";
        input.UserGrantId = "ug_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_user_grant_site_binding_mismatch");
    }

    [Fact]
    public void TryBuild_UserGrantExpired_Fails()
    {
        SeedUserGrant("ug_1", principalId: "prn_a", expiresAt: DateTime.UtcNow.AddHours(-1));
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        input.UserGrantId = "ug_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_user_grant_expired");
    }

    // ── system binding lookups ──

    [Fact]
    public void TryBuild_SystemBindingNotInDb_Fails()
    {
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        input.SystemBindingId = "syb_missing";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_system_binding_not_found");
    }

    [Fact]
    public void TryBuild_SystemBindingNotActive_Fails()
    {
        SeedSystemBinding("syb_1", status: "revoked");
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        input.SystemBindingId = "syb_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_system_binding_not_found");
    }

    [Fact]
    public void TryBuild_SystemBindingSiteMismatch_Fails()
    {
        SeedSiteBinding("sb_1", identityMode: "anonymous");
        SeedSystemBinding("syb_1", siteBindingId: "sb_other");
        Register(ToolId, BrowserSpec(identityMode: "anonymous"));
        var input = ValidInput();
        input.SiteBindingId = "sb_1";
        input.SystemBindingId = "syb_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_system_binding_site_mismatch");
    }

    // ── session lease lookups ──

    [Fact]
    public void TryBuild_SessionLeaseNotInDb_Fails()
    {
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        input.SessionLeaseId = "sl_missing";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_session_lease_not_found");
    }

    [Fact]
    public void TryBuild_SessionLeaseNotActive_Fails()
    {
        SeedSessionLease("sl_1", principalId: "prn_a", identityMode: "anonymous", leaseState: "released");
        Register(ToolId, BrowserSpec(identityMode: "anonymous"));
        var input = ValidInput();
        input.SessionLeaseId = "sl_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_session_lease_not_found");
    }

    [Fact]
    public void TryBuild_SessionLeasePrincipalMismatch_Fails()
    {
        SeedSessionLease("sl_1", principalId: "prn_other", identityMode: "anonymous");
        Register(ToolId, BrowserSpec(identityMode: "anonymous"));
        var input = ValidInput();
        input.SessionLeaseId = "sl_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_session_lease_principal_mismatch");
    }

    [Fact]
    public void TryBuild_SessionLeaseIdentityModeMismatch_Fails()
    {
        SeedSessionLease("sl_1", principalId: "prn_a", identityMode: "system_account");
        Register(ToolId, BrowserSpec(identityMode: "anonymous"));
        var input = ValidInput();
        input.SessionLeaseId = "sl_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_session_lease_identity_mismatch");
    }

    [Fact]
    public void TryBuild_SessionLeaseExpired_Fails()
    {
        SeedSessionLease("sl_1", principalId: "prn_a", identityMode: "anonymous",
            expiresAt: DateTime.UtcNow.AddHours(-1));
        Register(ToolId, BrowserSpec(identityMode: "anonymous"));
        var input = ValidInput();
        input.SessionLeaseId = "sl_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_session_lease_expired");
    }

    [Fact]
    public void TryBuild_SessionLeaseSiteMismatch_Fails()
    {
        SeedSiteBinding("sb_1", identityMode: "anonymous");
        SeedSessionLease("sl_1", principalId: "prn_a", identityMode: "anonymous", siteBindingId: "sb_other");
        Register(ToolId, BrowserSpec(identityMode: "anonymous"));
        var input = ValidInput();
        input.SiteBindingId = "sb_1";
        input.SessionLeaseId = "sl_1";
        Fails(_sut.TryBuild(ToolId, input), "browser_request_session_lease_site_mismatch");
    }

    // ── success: field mapping + defaults ──

    [Fact]
    public void TryBuild_MinimalAnonymous_Succeeds_MapsSpecAndInputFields()
    {
        var spec = BrowserSpec(
            identityMode: "anonymous",
            credentialBinding: "none",
            bindingMode: "ephemeral",
            reuseScope: "task",
            siteBindingMode: "public_open",
            allowedSiteClasses: new[] { "search" },
            maxActionLevel: "navigate",
            requiresHumanConfirmationOn: new[] { "committed_action" });
        Register(ToolId, spec);

        var input = ValidInput();
        var result = _sut.TryBuild(ToolId, input);

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        var req = result.Request!;

        // input-sourced
        req.RequestId.Should().Be("req_1");
        req.CapabilityId.Should().Be("cap_1");
        req.Route.Should().Be("browse");
        req.PrincipalId.Should().Be("prn_a");
        req.TaskId.Should().Be("task_a");
        req.SessionId.Should().Be("ses_a");
        req.StartUrl.Should().Be("https://example.com");
        req.IntendedActionLevel.Should().Be("read");

        // spec-sourced
        req.ToolId.Should().Be(ToolId);
        req.IdentityMode.Should().Be("anonymous");
        req.CredentialBinding.Should().Be("none");
        req.SessionBindingMode.Should().Be("ephemeral");
        req.SessionReuseScope.Should().Be("task");
        req.SiteBindingMode.Should().Be("public_open");
        req.AllowedSiteClasses.Should().BeEquivalentTo(new[] { "search" });
        req.MaxActionLevel.Should().Be("navigate");
        req.RequiresHumanConfirmationOn.Should().BeEquivalentTo(new[] { "committed_action" });
    }

    [Fact]
    public void TryBuild_BlankArgumentsAndScope_DefaultToEmptyJsonObject()
    {
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        input.ArgumentsJson = "   ";
        input.ScopeJson = "";

        var req = _sut.TryBuild(ToolId, input).Request!;
        req.ArgumentsJson.Should().Be("{}");
        req.ScopeJson.Should().Be("{}");
    }

    [Fact]
    public void TryBuild_NonBlankArgumentsAndScope_PassedThrough()
    {
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        input.ArgumentsJson = "{\"q\":\"hi\"}";
        input.ScopeJson = "{\"scope\":1}";

        var req = _sut.TryBuild(ToolId, input).Request!;
        req.ArgumentsJson.Should().Be("{\"q\":\"hi\"}");
        req.ScopeJson.Should().Be("{\"scope\":1}");
    }

    [Fact]
    public void TryBuild_OptionalBindingIdsBlank_AreNotValidatedAndPassedThrough()
    {
        Register(ToolId, BrowserSpec());
        var input = ValidInput();
        input.SiteBindingId = null;
        input.UserGrantId = null;
        input.SystemBindingId = null;
        input.SessionLeaseId = null;

        var result = _sut.TryBuild(ToolId, input);
        result.Success.Should().BeTrue();
        result.Request!.SiteBindingId.Should().BeNull();
        result.Request.UserGrantId.Should().BeNull();
        result.Request.SystemBindingId.Should().BeNull();
        result.Request.SessionLeaseId.Should().BeNull();
    }

    [Fact]
    public void TryBuild_FullUserDelegatedChain_Succeeds()
    {
        SeedSiteBinding("sb_1", identityMode: "user_delegated", siteClass: "search", principalId: "prn_a");
        SeedUserGrant("ug_1", principalId: "prn_a", siteBindingId: "sb_1");
        Register(ToolId, BrowserSpec(
            identityMode: "user_delegated",
            credentialBinding: "user_grant",
            siteBindingMode: "user_authorized_site",
            allowedSiteClasses: new[] { "search" }));

        var input = ValidInput();
        input.SiteBindingId = "sb_1";
        input.UserGrantId = "ug_1";

        var result = _sut.TryBuild(ToolId, input);
        result.Success.Should().BeTrue();
        var req = result.Request!;
        req.IdentityMode.Should().Be("user_delegated");
        req.SiteBindingId.Should().Be("sb_1");
        req.UserGrantId.Should().Be("ug_1");
    }

    // ── seed helpers ──

    private void SeedSiteBinding(
        string id,
        string identityMode = "anonymous",
        string siteClass = "search",
        string status = "active",
        string? principalId = null)
    {
        _db.Insert(new BrowserSiteBinding
        {
            SiteBindingId = id,
            IdentityMode = identityMode,
            SiteClass = siteClass,
            Status = status,
            PrincipalId = principalId
        });
    }

    private void SeedUserGrant(
        string id,
        string principalId,
        string? siteBindingId = null,
        string status = "active",
        DateTime? expiresAt = null)
    {
        _db.Insert(new BrowserUserGrant
        {
            UserGrantId = id,
            PrincipalId = principalId,
            SiteBindingId = siteBindingId,
            Status = status,
            ExpiresAt = expiresAt
        });
    }

    private void SeedSystemBinding(
        string id,
        string? siteBindingId = null,
        string status = "active")
    {
        _db.Insert(new BrowserSystemBinding
        {
            SystemBindingId = id,
            SiteBindingId = siteBindingId,
            Status = status
        });
    }

    private void SeedSessionLease(
        string id,
        string principalId,
        string identityMode,
        string leaseState = "active",
        string? siteBindingId = null,
        DateTime? expiresAt = null)
    {
        _db.Insert(new BrowserSessionLease
        {
            SessionLeaseId = id,
            PrincipalId = principalId,
            IdentityMode = identityMode,
            LeaseState = leaseState,
            SiteBindingId = siteBindingId,
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddHours(1)
        });
    }
}
