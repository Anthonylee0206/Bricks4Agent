using System.Net;
using System.Text.Json;
using Broker.Endpoints;
using Broker.Middleware;
using Broker.NyaChat;
using Broker.Services;
using BrokerCore.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Unit.Tests.Admin;

public class NyaChatEndpointAuthorizationTests
{
    [Theory]
    [InlineData(false, 3)]
    [InlineData(true, 2)]
    public async Task MemoryEndpoints_ReflectExpiryForgottenAndRecentHistory(bool autoSummarize, int expectedHistory)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nya_memory_admin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var brokerDb = BrokerDb.UseSqlite($"Data Source={Path.Combine(directory, "broker.db")};Pooling=False");
            new BrokerDbInitializer(brokerDb).Initialize();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.Configuration["NyaChat:DataDirectory"] = Path.Combine(directory, "tools");
            builder.Services.AddSingleton(brokerDb);
            builder.Services.AddSingleton<LocalAdminAuthService>();
            builder.Services.AddNyaChat(new NyaChatConfig
            {
                DbPath = Path.Combine(directory, "nya.db") + ";Pooling=False",
                SoulsDirectory = Path.Combine(directory, "souls"),
                PromptsDirectory = Path.Combine(directory, "prompts"),
                AutoSummarizeEnabled = autoSummarize,
                ShortTermMessageLimit = 3
            });
            await using var app = builder.Build();
            NyaChatEndpoints.Map(app.MapGroup("/api/v1"));
            var auth = app.Services.GetRequiredService<LocalAdminAuthService>();
            var login = new DefaultHttpContext();
            login.Connection.RemoteIpAddress = IPAddress.Loopback;
            auth.Login(login, "admin", "admin", "admin-test-password").Authenticated.Should().BeTrue();
            var cookie = login.Response.Headers.SetCookie.Single()!.Split(';', 2)[0];
            var memory = app.Services.GetRequiredService<NyaMemoryService>();
            var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>().ToArray();

            async Task<(int Status, JsonElement Data)> Request(string method, string template,
                string path, Dictionary<string, object?>? values = null, string? body = null, string? session = null)
            {
                var endpoint = endpoints.Single(e => e.RoutePattern.RawText == "/api/v1/local-admin/nya" + template &&
                    e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));
                var ctx = new DefaultHttpContext { RequestServices = app.Services };
                ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
                ctx.Request.Method = method;
                ctx.Request.Path = "/api/v1/local-admin/nya" + path;
                ctx.Request.Headers.Cookie = session ?? cookie;
                ctx.Request.RouteValues = new RouteValueDictionary(values ?? new());
                if (body != null) ctx.Items[EncryptionMiddleware.DecryptedBodyKey] = body;
                using var stream = new MemoryStream();
                ctx.Response.Body = stream;
                await endpoint.RequestDelegate!(ctx);
                stream.Position = 0;
                using var response = await JsonDocument.ParseAsync(stream);
                return (ctx.Response.StatusCode, response.RootElement.TryGetProperty("data", out var data) ? data.Clone() : default);
            }

            const string userId = "line:memory-user";
            var userValues = new Dictionary<string, object?> { ["userId"] = userId };
            var before = DateTime.UtcNow;
            var add = await Request("POST", "/users/{userId}/facts", $"/users/{userId}/facts", userValues,
                "{\"category\":\"context\",\"key\":\"plan\",\"value\":\"interview\",\"source_text\":\"Tomorrow is my interview\",\"expires_in_days\":2}");
            add.Status.Should().Be(200);
            add.Data.GetProperty("source_text").GetString().Should().Be("Tomorrow is my interview");
            var expiry = add.Data.GetProperty("expires_at").GetDateTime();
            expiry.Kind.Should().Be(DateTimeKind.Utc);
            expiry.Should().BeOnOrAfter(before.AddDays(2)).And.BeOnOrBefore(DateTime.UtcNow.AddDays(2));

            var permanent = await Request("POST", "/users/{userId}/facts", $"/users/{userId}/facts", userValues,
                "{\"category\":\"identity\",\"key\":\"name\",\"value\":\"NYA\"}");
            permanent.Status.Should().Be(200);
            permanent.Data.GetProperty("expires_at").ValueKind.Should().Be(JsonValueKind.Null);
            permanent.Data.GetProperty("source_text").ValueKind.Should().Be(JsonValueKind.Null);
            foreach (var invalid in new[] { "0", "-1", "1.5", "\"2\"", "2147483647" })
            {
                var bad = await Request("POST", "/users/{userId}/facts", $"/users/{userId}/facts", userValues,
                    $"{{\"category\":\"context\",\"key\":\"bad\",\"value\":\"bad\",\"expires_in_days\":{invalid}}}");
                bad.Status.Should().Be(400);
            }
            memory.GetFactHistory(userId, "bad").Should().BeEmpty();
            memory.UpsertFact(userId, "context", "expired", "old", sourceText: "Original old words", expiresAt: before.AddDays(-1));
            var notes = await Request("GET", "/users/{userId}/facts", $"/users/{userId}/facts", userValues);
            notes.Status.Should().Be(200);
            notes.Data.GetProperty("total").GetInt32().Should().Be(3);
            var expired = notes.Data.GetProperty("facts").EnumerateArray().Single(f => f.GetProperty("fact_key").GetString() == "expired");
            expired.GetProperty("source_text").GetString().Should().Be("Original old words");
            expired.GetProperty("expires_at").GetDateTime().Kind.Should().Be(DateTimeKind.Utc);
            expired.GetProperty("is_expired").GetBoolean().Should().BeTrue();
            memory.GetActiveFacts(userId).Should().HaveCount(2);

            memory.ForgetFact(userId, "plan").Should().BeTrue();
            memory.ForgetFact(userId, "expired").Should().BeTrue();
            memory.UpsertFact("other-user", "context", "plan", "other");
            memory.ForgetFact("other-user", "plan");
            auth.CreateOperator("audit", "Audit", LocalAdminRoles.Auditor, "audit-test-password", "local_admin");
            var auditLogin = new DefaultHttpContext();
            auditLogin.Connection.RemoteIpAddress = IPAddress.Loopback;
            auth.Login(auditLogin, "audit", "audit-test-password", null).Authenticated.Should().BeTrue();
            var auditCookie = auditLogin.Response.Headers.SetCookie.Single()!.Split(';', 2)[0];
            var forgotten = await Request("GET", "/users/{userId}/forgotten", $"/users/{userId}/forgotten", userValues, session: auditCookie);
            forgotten.Status.Should().Be(200);
            forgotten.Data.GetProperty("forgotten").GetArrayLength().Should().Be(2);
            forgotten.Data.GetProperty("forgotten").EnumerateArray().Should().Contain(f =>
                f.GetProperty("fact_key").GetString() == "plan" && f.GetProperty("fact_value").GetString() == "interview" &&
                f.GetProperty("created_at").ValueKind == JsonValueKind.String);
            var factValues = new Dictionary<string, object?> { ["userId"] = userId, ["factKey"] = "plan" };
            var denied = await Request("DELETE", "/users/{userId}/forgotten/{factKey}", $"/users/{userId}/forgotten/plan", factValues, session: auditCookie);
            denied.Status.Should().Be(403);
            var clear = await Request("DELETE", "/users/{userId}/forgotten/{factKey}", $"/users/{userId}/forgotten/plan", factValues);
            clear.Status.Should().Be(200);
            memory.GetForgotten(userId).Should().ContainSingle(f => f.FactKey == "expired");
            memory.GetForgotten("other-user").Should().ContainSingle();
            memory.GetFactHistory(userId, "plan").Should().BeEmpty();
            var missing = await Request("DELETE", "/users/{userId}/forgotten/{factKey}", $"/users/{userId}/forgotten/plan", factValues);
            missing.Status.Should().Be(404);
            memory.UpsertFact(userId, "context", "expired", "relearned");
            memory.GetForgotten(userId).Should().BeEmpty();

            memory.AppendMessage(userId, "user", "old summarized", "topic-a");
            memory.AppendMessage(userId, "assistant", "old reply", "topic-a");
            memory.MarkAsSummarized(userId, "topic-a", 1, 2);
            memory.AppendMessage(userId, "user", "recent", "topic-b");
            memory.AppendMessage(userId, "assistant", "recent reply", "topic-b");
            memory.CreateSummary(userId, "topic-a", "existing summary", 1, 2, 2);
            var overview = await Request("GET", "/users/{userId}/overview", $"/users/{userId}/overview", userValues);
            overview.Status.Should().Be(200);
            overview.Data.GetProperty("messages").GetProperty("recent_history").GetInt32().Should().Be(expectedHistory);
            overview.Data.GetProperty("auto_summarize_enabled").GetBoolean().Should().Be(autoSummarize);
            var status = await Request("GET", "/status", "/status");
            status.Status.Should().Be(200);
            status.Data.GetProperty("auto_summarize_enabled").GetBoolean().Should().Be(autoSummarize);
            var summaries = await Request("GET", "/users/{userId}/summaries", $"/users/{userId}/summaries", userValues);
            summaries.Data.GetProperty("auto_summarize_enabled").GetBoolean().Should().Be(autoSummarize);
            summaries.Data.GetProperty("summaries").GetArrayLength().Should().Be(1);
            if (!autoSummarize)
            {
                var trigger = await Request("POST", "/users/{userId}/summaries/trigger", $"/users/{userId}/summaries/trigger", userValues);
                trigger.Status.Should().Be(200);
                trigger.Data.GetProperty("triggered").GetBoolean().Should().BeFalse();
                trigger.Data.GetProperty("status").GetString().Should().Be("Disabled");
                memory.GetSummaryHistory(userId).Should().ContainSingle();
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData("/users/user-1/soul")]
    [InlineData("/users/web%3Auser%2F1/soul")]
    public void SoulUnbind_RequiresUserManage(string path)
    {
        NyaChatEndpoints.TryResolveRouteAuthorization("DELETE", path, out var permissions, out var superAdminOnly)
            .Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.PermissionUserManage);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("/users")]
    [InlineData("/users/user-1/overview")]
    [InlineData("/users/user-1/messages")]
    [InlineData("/users/user-1/messages/message-1")]
    [InlineData("/users/user-1/topics")]
    [InlineData("/users/user-1/facts")]
    [InlineData("/users/user-1/forgotten")]
    [InlineData("/users/web%3Auser%2F1/forgotten")]
    [InlineData("/users/user-1/facts/fact-1/history")]
    [InlineData("/users/user-1/summaries")]
    public void UserReads_RequireUserManageOrAuditRead(string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization("GET", path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.PermissionUserManage, LocalAdminPermissions.AuditRead);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("POST", "/users/user-1/facts")]
    [InlineData("PUT", "/users/user-1/facts/fact-1")]
    [InlineData("POST", "/users/user-1/facts/fact-1/rollback")]
    [InlineData("DELETE", "/users/user-1/facts")]
    [InlineData("DELETE", "/users/user-1/facts/fact-1")]
    [InlineData("DELETE", "/users/user-1/forgotten/fact-1")]
    [InlineData("DELETE", "/users/web%3Auser%2F1/forgotten/name%2Fkey")]
    [InlineData("POST", "/users/user-1/messages")]
    [InlineData("PUT", "/users/user-1/messages/message-1")]
    [InlineData("DELETE", "/users/user-1/messages")]
    [InlineData("DELETE", "/users/user-1/messages/message-1")]
    [InlineData("POST", "/users/user-1/messages/soft-delete")]
    [InlineData("POST", "/users/user-1/topics")]
    [InlineData("PUT", "/users/user-1/topics/topic-1")]
    [InlineData("POST", "/users/user-1/topics/topic-1/switch")]
    [InlineData("POST", "/users/user-1/topics/topic-1/rename")]
    [InlineData("DELETE", "/users/user-1/topics/topic-1")]
    [InlineData("POST", "/users/user-1/summaries/trigger")]
    [InlineData("POST", "/users/user-1/summaries/summary-1/rollback")]
    [InlineData("PUT", "/users/user-1/summaries")]
    [InlineData("DELETE", "/users/user-1/summaries")]
    [InlineData("POST", "/users/user-1/soul")]
    [InlineData("PUT", "/users/user-1/soul")]
    [InlineData("DELETE", "/users/user-1/soul")]
    [InlineData("DELETE", "/users/user-1")]
    public void UserMutations_RequireUserManage(string method, string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.PermissionUserManage);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("/audit")]
    [InlineData("/users/user-1/audit")]
    public void AuditReads_RequireAuditRead(string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization("GET", path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.AuditRead);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("/status")]
    [InlineData("/config")]
    [InlineData("/prompts")]
    [InlineData("/tools")]
    [InlineData("/souls")]
    [InlineData("/souls/soul-1")]
    public void SystemReads_RequireStatusReadOrAuditRead(string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization("GET", path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.SystemStatusRead, LocalAdminPermissions.AuditRead);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("PUT", "/config")]
    [InlineData("POST", "/prompts/reload")]
    [InlineData("POST", "/tools/tool-1/toggle")]
    [InlineData("POST", "/souls")]
    [InlineData("POST", "/souls/reload")]
    [InlineData("PUT", "/souls/soul-1")]
    [InlineData("DELETE", "/souls/soul-1")]
    public void SystemMutations_RequireWorkerManage(string method, string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.SystemWorkerManage);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("/llm/param-catalog")]
    [InlineData("/llm/templates")]
    [InlineData("/llm/call-sites")]
    [InlineData("/llm/profiles")]
    [InlineData("/llm/routes")]
    public void LlmReads_RequireStatusRead(string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization("GET", path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.SystemStatusRead);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("DELETE", "/audit")]
    [InlineData("DELETE", "/audit/log-1")]
    [InlineData("DELETE", "/users/user-1/audit")]
    [InlineData("POST", "/llm/profiles")]
    [InlineData("PUT", "/llm/profiles")]
    [InlineData("DELETE", "/llm/profiles")]
    [InlineData("POST", "/llm/profiles/profile-1")]
    [InlineData("PUT", "/llm/profiles/profile-1")]
    [InlineData("DELETE", "/llm/profiles/profile-1")]
    [InlineData("PUT", "/llm/routes")]
    public void AuditDeletionsAndLlmMutations_RequireSuperAdmin(string method, string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().BeEmpty();
        superAdminOnly.Should().BeTrue();
    }

    [Theory]
    [InlineData("/roles")]
    [InlineData("/roles/role-1")]
    [InlineData("/capabilities")]
    [InlineData("/users/user-1/roles")]
    [InlineData("/users/user-1/roles/role-1")]
    public void RoleRoutes_AreDeniedForEveryMethod(string path)
    {
        foreach (var method in new[] { "GET", "POST", "PUT", "DELETE" })
        {
            var resolved = NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var superAdminOnly);

            resolved.Should().BeFalse();
            permissions.Should().BeEmpty();
            superAdminOnly.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("GET", "/unknown")]
    [InlineData("POST", "/users/user-1/unknown")]
    [InlineData("DELETE", "/users/user-1/audit/log-1")]
    [InlineData("GET", "/audit/log-1")]
    [InlineData("GET", "/users/user-1/facts/fact-1")]
    [InlineData("GET", "/users/user-1/forgotten/fact-1")]
    [InlineData("POST", "/users/user-1/forgotten")]
    [InlineData("POST", "/users/user-1/forgotten/fact-1")]
    [InlineData("PUT", "/users/user-1/forgotten/fact-1")]
    [InlineData("DELETE", "/users/user-1/forgotten")]
    [InlineData("GET", "/llm/profiles/profile-1")]
    [InlineData("DELETE", "/llm/routes")]
    [InlineData("POST", "/config")]
    [InlineData("PUT", "/prompts/reload")]
    [InlineData("PATCH", "/users/user-1/facts")]
    [InlineData("POST", "/users")]
    [InlineData("GET", "/users-extra")]
    [InlineData("GET", "/users/user-1/messages/message-1/unknown")]
    [InlineData("GET", "/users//messages")]
    [InlineData("GET", "/local-admin/nya/status")]
    [InlineData("GET", "status")]
    [InlineData("GET", "")]
    [InlineData("GET", "/")]
    public void UnmatchedRoutes_AreDenied(string method, string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var superAdminOnly);

        resolved.Should().BeFalse();
        permissions.Should().BeEmpty();
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("get", "/UsErS/User-1/MeSsAgEs/")]
    [InlineData("GET", "/users/%E7%94%A8%E6%88%B6/messages/message-1")]
    public void RouteMatching_AcceptsCaseAndTrailingSlashAndEncodedIdentifiers(string method, string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.PermissionUserManage, LocalAdminPermissions.AuditRead);
        superAdminOnly.Should().BeFalse();
    }
}
