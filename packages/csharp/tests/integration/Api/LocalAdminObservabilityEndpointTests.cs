using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Broker.Services;
using Broker.Services.Observability;
using Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Integration.Tests.Api;

/// <summary>
/// 管理後台用的觀測 API（/api/v1/local-admin/observability/*）：照 local-admin 的規格，
/// 用 cookie 工作階段與 system.monitor.read 權限把關，管理後台與 observability.html 才叫得到。
/// </summary>
public sealed class LocalAdminObservabilityEndpointTests : IClassFixture<IncidentReportFixture>
{
    private const string Base = "/api/v1/local-admin/observability";

    private readonly IncidentReportFixture _fixture;

    public LocalAdminObservabilityEndpointTests(IncidentReportFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SystemAdmin_CanReadHealthAndLlm_AndRunAndReadAReport()
    {
        var cookie = await LoginAsNewOperatorAsync(LocalAdminRoles.SystemAdmin);

        foreach (var path in new[] { "/health-score", "/health-score/history", "/workers", "/llm", "/incident-reports" })
        {
            using var response = await SendAsync(HttpMethod.Get, Base + path, cookie);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "{0} should be readable with system.monitor.read", path);
        }

        // 和管理後台一樣送明文 JSON（加密層把 /api/v1/local-admin/ 列為明文路徑）
        using var run = await SendAsync(HttpMethod.Post, Base + "/incident-reports/run", cookie, new { minutes = 5 });
        run.StatusCode.Should().Be(HttpStatusCode.OK, await run.Content.ReadAsStringAsync());
        using var runDoc = JsonDocument.Parse(await run.Content.ReadAsStringAsync());
        var reportId = runDoc.RootElement.GetProperty("data").GetProperty("report_id").GetString();
        reportId.Should().StartWith("incident_");

        using var detail = await SendAsync(HttpMethod.Get, $"{Base}/incident-reports/{reportId}", cookie);
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        (await detail.Content.ReadAsStringAsync()).Should().Contain("blind_spots");
    }

    [Fact]
    public async Task PermissionAdmin_WithoutMonitorPermission_IsForbidden()
    {
        var cookie = await LoginAsNewOperatorAsync(LocalAdminRoles.PermissionAdmin);

        using var list = await SendAsync(HttpMethod.Get, Base + "/incident-reports", cookie);
        list.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var run = await SendAsync(HttpMethod.Post, Base + "/incident-reports/run", cookie, new { minutes = 5 });
        run.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WithoutALocalAdminSession_IsRejected()
    {
        using var response = await SendAsync(HttpMethod.Get, Base + "/health-score", cookie: null);

        ((int)response.StatusCode).Should().BeOneOf(401, 403);
    }

    [Fact]
    public async Task RemoteRequests_AreRejected_EvenWithAValidCookie()
    {
        // local-admin 只接受本機連線；新的端點沿用同一個驗證，不能從外面叫
        var cookie = await LoginAsNewOperatorAsync(LocalAdminRoles.SystemAdmin);

        using var response = await SendAsync(HttpMethod.Get, Base + "/health-score", cookie, remoteAddress: "203.0.113.10");

        ((int)response.StatusCode).Should().BeOneOf(401, 403);
    }

    [Fact]
    public async Task ObservabilityPage_IsServed()
    {
        using var response = await _fixture.Client.GetAsync("/observability.html");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("系統健康與異常報告");
    }

    private Task<string> LoginAsNewOperatorAsync(string role)
        => LocalAdminTestClient.LoginAsNewOperatorAsync(_fixture, role);

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? cookie, object? body = null, string? remoteAddress = null)
        => LocalAdminTestClient.SendAsync(_fixture, method, path, cookie, body, remoteAddress);
}

/// <summary>
/// Function Pool 沒開（預設設定）時：worker 相關端點回「未啟用」的正常回應，
/// 不能被 R6 當成伺服器錯誤。observability.html 每次載入都會讀健康分數，原本回 503，等於觀測頁自己製造誤報。
/// </summary>
public sealed class LocalAdminObservabilityPoolDisabledTests : IClassFixture<PoolDisabledObservabilityFixture>
{
    private const string Base = "/api/v1/local-admin/observability";

    private readonly PoolDisabledObservabilityFixture _fixture;

    public LocalAdminObservabilityPoolDisabledTests(PoolDisabledObservabilityFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WorkerEndpoints_ReportPoolDisabled_WithoutRaisingAServerErrorFinding()
    {
        var cookie = await LocalAdminTestClient.LoginAsNewOperatorAsync(_fixture, LocalAdminRoles.SystemAdmin);
        var start = DateTime.UtcNow;

        foreach (var path in new[] { "/health-score", "/health-score/history", "/workers" })
        {
            using var response = await LocalAdminTestClient.SendAsync(_fixture, HttpMethod.Get, Base + path, cookie);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "{0}: a disabled Function Pool is a setting, not a server error", path);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            doc.RootElement.GetProperty("data").GetProperty("pool_enabled").GetBoolean().Should().BeFalse();
        }

        await Task.Delay(1100);   // HTTP 計數以秒分桶；讓最後一秒的請求落在時間窗內
        var report = await _fixture.Services.GetRequiredService<IncidentReportService>()
            .RunAsync(start, DateTime.UtcNow, IncidentReportTriggers.Manual);

        report.Findings.Should().NotContain(f => f.RuleId == "R6", "opening the observability page must not look like a server error");
    }
}

/// <summary>預設設定的主機（Function Pool 關閉）；不跑 RagSeed、不呼叫 LLM。</summary>
public sealed class PoolDisabledObservabilityFixture : BrokerAuthorizationFixture
{
    public PoolDisabledObservabilityFixture()
        : base(enforceWorkerAuth: true, hostSettings: new Dictionary<string, string?>
        {
            ["Observability:IncidentReport:Narrate"] = "false",
            ["RagSeed:Enabled"] = "false",
        })
    {
    }
}

/// <summary>以 local-admin 的方式登入與送請求：cookie 工作階段、明文 JSON。</summary>
internal static class LocalAdminTestClient
{
    public static async Task<string> LoginAsNewOperatorAsync(BrokerAuthorizationFixture fixture, string role)
    {
        var username = $"obs_{Guid.NewGuid():N}"[..16];
        using (var scope = fixture.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<LocalAdminAuthService>()
                .CreateOperator(username, username, role, "observability-password", "test");
        }

        using var response = await fixture.Client.PostAsJsonAsync("/api/v1/local-admin/login", new
        {
            username,
            password = "observability-password",
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
        var cookie = values!.First(v => v.StartsWith($"{LocalAdminAuthService.SessionCookieName}=", StringComparison.Ordinal));
        return cookie.Split(';', 2)[0];
    }

    public static Task<HttpResponseMessage> SendAsync(
        BrokerAuthorizationFixture fixture, HttpMethod method, string path, string? cookie,
        object? body = null, string? remoteAddress = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);
        if (remoteAddress is not null)
            request.Headers.Add(RemoteAddressStartupFilter.HeaderName, remoteAddress);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return fixture.Client.SendAsync(request);
    }
}
