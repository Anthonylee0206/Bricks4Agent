using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Broker.Services;
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

    private async Task<string> LoginAsNewOperatorAsync(string role)
    {
        var username = $"obs_{Guid.NewGuid():N}"[..16];
        using (var scope = _fixture.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<LocalAdminAuthService>()
                .CreateOperator(username, username, role, "observability-password", "test");
        }

        using var response = await _fixture.Client.PostAsJsonAsync("/api/v1/local-admin/login", new
        {
            username,
            password = "observability-password",
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();
        var cookie = values!.First(v => v.StartsWith($"{LocalAdminAuthService.SessionCookieName}=", StringComparison.Ordinal));
        return cookie.Split(';', 2)[0];
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? cookie, object? body = null, string? remoteAddress = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (cookie is not null)
            request.Headers.Add("Cookie", cookie);
        if (remoteAddress is not null)
            request.Headers.Add(RemoteAddressStartupFilter.HeaderName, remoteAddress);
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return _fixture.Client.SendAsync(request);
    }
}
