using Broker.Services;
using Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Integration.Tests.Api;

/// <summary>
/// 使用者審批頁（user-approvals.html）的核准／拒絕：頁面只有簽章連結 token，送的是明文 JSON。
/// 原本兩個 POST 沒有標 SignedLink（套用預設 ScopedToken），加密層也要求加密信封，
/// 請求一律在中介層被擋成 400、到不了 handler。這組測試確認請求會到 handler，由它驗證 token。
/// </summary>
public sealed class UserApprovalLinkPostTests : IClassFixture<UserApprovalLinkFixture>
{
    private const string UserId = "U-approval-link-test";

    private readonly UserApprovalLinkFixture _fixture;
    private readonly EncryptedBrokerClient _client;

    public UserApprovalLinkPostTests(UserApprovalLinkFixture fixture)
    {
        _fixture = fixture;
        _client = new EncryptedBrokerClient(fixture.Client, fixture.Services);
    }

    private string ValidToken()
    {
        var token = _fixture.Services.GetRequiredService<ApprovalLinkService>().CreateToken(UserId);
        token.Should().NotBeNull("the fixture configures ArtifactDownload:SigningSecret");
        return token!;
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task InvalidLink_IsRejectedByTheHandler_NotBlockedByMiddleware(string action)
    {
        var reply = await _client.SendPlainAsync("POST", $"/api/v1/user/approvals/apr_missing/{action}",
            "{\"token\":\"not-a-valid-link\",\"reason\":\"test\"}");

        reply.Status.Should().Be(401, "the response was {0}", reply);
        reply.Message.Should().Be("Invalid or expired link.");
    }

    [Fact]
    public async Task ValidLink_ReachesTheBroker_ForAnApprovalThatDoesNotExist()
    {
        var reply = await _client.SendPlainAsync("POST", "/api/v1/user/approvals/apr_missing/approve",
            $"{{\"token\":\"{ValidToken()}\",\"reason\":\"test\"}}");

        reply.Status.Should().Be(404, "the token is valid, so the handler looks the approval up: {0}", reply);
    }

    [Fact]
    public async Task ValidLink_ListsTheUsersPendingApprovals()
    {
        var reply = await _client.SendPlainAsync("GET", $"/api/v1/user/approvals?token={Uri.EscapeDataString(ValidToken())}");

        reply.Status.Should().Be(200, "the response was {0}", reply);
        reply.Body.Should().Contain(UserId);
    }

    [Fact]
    public async Task OtherPosts_StillRequireTheEncryptedEnvelope()
    {
        // 白名單只加了 /api/v1/user/approvals/，其他 POST 仍然必須是加密信封
        var reply = await _client.SendPlainAsync("POST", "/api/v1/execution-requests/submit",
            "{\"capability_id\":\"x\",\"idempotency_key\":\"k\"}");

        reply.Status.Should().Be(400, "the response was {0}", reply);
    }
}

/// <summary>授權測試主機加上簽章連結的金鑰（appsettings 預設是空的，空的時候任何連結都驗不過）。</summary>
public sealed class UserApprovalLinkFixture : BrokerAuthorizationFixture
{
    public UserApprovalLinkFixture()
        : base(
            enforceWorkerAuth: true,
            hostSettings: new Dictionary<string, string?>
            {
                ["ArtifactDownload:SigningSecret"] = "integration-test-approval-link-signing-secret",
            })
    {
    }
}
