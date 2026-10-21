using Broker.NyaChat.Abstractions;
using System.Net;
using System.Text.Json;
using Broker.Endpoints;
using Broker.Middleware;
using Broker.NyaChat;
using Broker.Services;
using BrokerCore.Data;
using BrokerCore.Models;
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
    [InlineData(false, 2)]
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
                string path, Dictionary<string, object?>? values = null, string? body = null, string? session = null, string? query = null)
            {
                var endpoint = endpoints.Single(e => e.RoutePattern.RawText == "/api/v1/local-admin/nya" + template &&
                    e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));
                var ctx = new DefaultHttpContext { RequestServices = app.Services };
                ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
                ctx.Request.Method = method;
                ctx.Request.Path = "/api/v1/local-admin/nya" + path;
                if (query != null) ctx.Request.QueryString = new QueryString(query);
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
            var add = await Request("POST", "/users/{userId}/notes", $"/users/{userId}/notes", userValues,
                "{\"key\":\"plan\",\"content\":\"interview\",\"expiry\":{\"mode\":\"on_date\",\"date\":\"2099-01-01\"}}");
            add.Status.Should().Be(200);
            var planId = add.Data.GetProperty("data").GetProperty("note_id").GetString()!;
            add.Data.GetProperty("data").GetProperty("expires_at").GetDateTime().Should().Be(new DateTime(2099, 1, 1, 16, 0, 0, DateTimeKind.Utc));
            var permanent = await Request("POST", "/users/{userId}/notes", $"/users/{userId}/notes", userValues,
                "{\"key\":\"name\",\"content\":\"NYA\",\"expiry\":{\"mode\":\"never\"}}");
            permanent.Status.Should().Be(200);
            permanent.Data.GetProperty("data").GetProperty("expires_at").ValueKind.Should().Be(JsonValueKind.Null);
            foreach (var invalid in new[] { "0", "-1", "1.5", "\"days\"", "null" })
                (await Request("POST", "/users/{userId}/notes", $"/users/{userId}/notes", userValues,
                    $"{{\"key\":\"bad\",\"content\":\"bad\",\"expiry\":{invalid}}}")).Status.Should().Be(400);
            var expiredNote = memory.SaveNote(new global::Broker.NyaChat.Abstractions.NyaToolContext { UserId = userId }, null, "expired", "old", "on_date", "2000-01-01", []);
            var notes = await Request("GET", "/users/{userId}/notes", $"/users/{userId}/notes", userValues);
            notes.Data.GetProperty("total").GetInt32().Should().Be(3);
            notes.Data.GetProperty("notes").EnumerateArray().Single(n => n.GetProperty("key").GetString() == "expired").GetProperty("expired").GetBoolean().Should().BeTrue();
            memory.ListNotes(userId).Should().HaveCount(2);
            memory.SetSoulBinding("binding-only", "NYA");
            memory.CreateNewTopic("topic-only", "Empty room");
            var list = await Request("GET", "/users", "/users");
            var listed = list.Data.GetProperty("users").EnumerateArray().ToArray();
            listed.Should().Contain(u => u.GetProperty("user_id").GetString() == "binding-only");
            listed.Should().Contain(u => u.GetProperty("user_id").GetString() == "topic-only");
            listed.Single(u => u.GetProperty("user_id").GetString() == userId).GetProperty("active_note_count").GetInt32().Should().Be(2);
            var found = await Request("POST", "/users/{userId}/forget-find", $"/users/{userId}/forget-find", userValues,
                JsonSerializer.Serialize(new { note_ids = new[] { planId, expiredNote.NoteId } }));
            found.Status.Should().Be(200);
            var selectionId = found.Data.GetProperty("data").GetProperty("selection_id").GetString();
            var hidden = await Request("POST", "/users/{userId}/forget", $"/users/{userId}/forget", userValues, JsonSerializer.Serialize(new { selection_id = selectionId }));
            hidden.Status.Should().Be(200);
            var forgetId = hidden.Data.GetProperty("data").GetProperty("forget_id").GetString()!;
            hidden.Data.GetRawText().Should().NotContain("interview");
            auth.CreateOperator("audit", "Audit", LocalAdminRoles.Auditor, "audit-test-password", "local_admin");
            var auditLogin = new DefaultHttpContext();
            auditLogin.Connection.RemoteIpAddress = IPAddress.Loopback;
            auth.Login(auditLogin, "audit", "audit-test-password", null).Authenticated.Should().BeTrue();
            var auditCookie = auditLogin.Response.Headers.SetCookie.Single()!.Split(';', 2)[0];
            var references = await Request("GET", "/users/{userId}/hide-references", $"/users/{userId}/hide-references", userValues, session: auditCookie);
            references.Status.Should().Be(200);
            references.Data.GetProperty("references").GetArrayLength().Should().Be(2);
            references.Data.GetProperty("references").EnumerateArray().Should().AllSatisfy(r =>
            {
                r.GetProperty("forget_id").GetString().Should().Be(forgetId);
                r.TryGetProperty("is_active", out _).Should().BeTrue();
            });
            var forgetValues = new Dictionary<string, object?> { ["userId"] = userId, ["forgetId"] = forgetId };
            (await Request("POST", "/users/{userId}/hide-references/{forgetId}/unforget", $"/users/{userId}/hide-references/{forgetId}/unforget", forgetValues, session: auditCookie)).Status.Should().Be(403);
            (await Request("POST", "/users/{userId}/forget", $"/users/{userId}/forget", userValues, "{}", session: auditCookie)).Status.Should().Be(403);
            (await Request("POST", "/users/{userId}/hide-references/{forgetId}/unforget", $"/users/{userId}/hide-references/{forgetId}/unforget", forgetValues)).Status.Should().Be(200);
            memory.ListNotes(userId).Should().HaveCount(2);
            memory.AdminNotes(userId).Should().HaveCount(3);

            memory.AppendMessage(userId, "user", "old summarized", "topic-a");
            memory.AppendMessage(userId, "assistant", "old reply", "topic-a");
            memory.MarkAsSummarized(userId, "topic-a", 1, 2);
            var activeTopic = memory.CreateNewTopic(userId, "Current room");
            memory.AppendMessage(userId, "user", "recent", activeTopic.TopicId);
            memory.AppendMessage(userId, "assistant", "recent reply", activeTopic.TopicId);
            memory.CreateSummary(userId, "topic-a", "existing summary", 1, 2, 2);
            var overview = await Request("GET", "/users/{userId}/overview", $"/users/{userId}/overview", userValues);
            overview.Status.Should().Be(200);
            overview.Data.GetProperty("messages").GetProperty("recent_history").GetInt32().Should().Be(expectedHistory);
            overview.Data.GetProperty("active_topic").GetProperty("topic_id").GetString().Should().Be(activeTopic.TopicId);
            var emptyValues = new Dictionary<string, object?> { ["userId"] = "overview-only" };
            var empty = await Request("GET", "/users/{userId}/overview", "/users/overview-only/overview", emptyValues);
            empty.Data.GetProperty("active_topic").ValueKind.Should().Be(JsonValueKind.Null);
            memory.GetActiveTopic("overview-only").Should().BeNull();
            var sensitive = memory.AppendMessage(userId, "user", "secret note text", activeTopic.TopicId);
            memory.Forget(new NyaToolContext { UserId = userId }, null, [], [sensitive.MessageId]);
            var externalTurn = memory.BeginTurn(userId, "external-turn", "external-only", "external isolation", "external-only");
            memory.FinishTurn(externalTurn, "external reply");
            var external = memory.GetAllMessages(userId).Single(m => m.TurnId == externalTurn.TurnId && m.Role == "user");
            var legacy = memory.AppendMessage(userId, "user", "legacy words");
            var messages = await Request("GET", "/users/{userId}/messages", $"/users/{userId}/messages", userValues);
            messages.Data.GetProperty("external_conversations").EnumerateArray().Should().Contain(x => x.GetString() == "external-only");
            var diagnostics = messages.Data.GetProperty("messages").EnumerateArray().ToArray();
            diagnostics.Single(m => m.GetProperty("message_id").GetString() == sensitive.MessageId).GetProperty("exclusion_reasons").EnumerateArray().Should().Contain(r => r.GetString() == "forgotten");
            diagnostics.Single(m => m.GetProperty("message_id").GetString() == external.MessageId).GetProperty("exclusion_reasons").EnumerateArray().Should().Contain(r => r.GetString() == "other_conversation");
            var legacyList = await Request("GET", "/users/{userId}/messages", $"/users/{userId}/messages", userValues, query: "?unassigned=true");
            legacyList.Data.GetProperty("messages").EnumerateArray().Should().ContainSingle(m => m.GetProperty("message_id").GetString() == legacy.MessageId);
            var auditLogger = app.Services.GetRequiredService<NyaAuditLogger>();
            auditLogger.LogChatInput(userId, "turn input", "fake", 1, 0, 2, 4, traceId: "trace-admin", selection: new { history_ids = new[] { sensitive.MessageId } });
            auditLogger.LogChatOutput(userId, "output", "fake", 1, 1, traceId: "trace-admin");
            var trace = await Request("GET", "/audit/traces/{traceId}", "/audit/traces/trace-admin", new() { ["traceId"] = "trace-admin" }, session: auditCookie);
            trace.Status.Should().Be(200);
            trace.Data.GetProperty("logs").GetArrayLength().Should().Be(2);
            trace.Data.GetProperty("logs").EnumerateArray().Should().OnlyContain(l => l.GetProperty("trace_id").GetString() == "trace-admin");
            (await Request("GET", "/audit/traces/{traceId}", "/audit/traces/trace-admin", new() { ["traceId"] = "trace-admin" }, session: "")).Status.Should().Be(401);
            app.Services.GetRequiredService<NyaToolRegistry>().SetEnabled("note_save", false);
            var availability = await Request("GET", "/tools/availability", "/tools/availability", query: "?user_id=line%3Atest&channel_type=line", session: auditCookie);
            availability.Status.Should().Be(200);
            availability.Data.GetProperty("tools").EnumerateArray().Should().OnlyContain(t => t.GetProperty("usable").GetBoolean() == t.GetProperty("function_tool").GetBoolean());
            availability.Data.GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("id").GetString() == "note_save").GetProperty("reason").GetString().Should().Be("disabled");
            (await Request("GET", "/tools/availability", "/tools/availability")).Status.Should().Be(400);
            overview.Data.GetProperty("auto_summarize_enabled").GetBoolean().Should().Be(autoSummarize);
            var status = await Request("GET", "/status", "/status");
            status.Status.Should().Be(200);
            status.Data.GetProperty("auto_summarize_enabled").GetBoolean().Should().Be(autoSummarize);
            // 測試環境沒有設定任何 soul，清單可能是空的；有的話每一個都要用 snake_case 的 soul_id
            foreach (var soul in status.Data.GetProperty("available_souls").EnumerateArray())
                soul.TryGetProperty("soul_id", out _).Should().BeTrue();
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
            auth.CreateOperator("user-manager", "User manager", LocalAdminRoles.PermissionAdmin, "manager-test-password", "local_admin");
            var managerLogin = new DefaultHttpContext();
            managerLogin.Connection.RemoteIpAddress = IPAddress.Loopback;
            auth.Login(managerLogin, "user-manager", "manager-test-password", null).Authenticated.Should().BeTrue();
            var managerCookie = managerLogin.Response.Headers.SetCookie.Single()!.Split(';', 2)[0];
            var managerSession = brokerDb.GetAll<LocalAdminSession>().Single(s => s.Username == "user-manager");
            managerSession.PermissionsSnapshot = JsonSerializer.Serialize(new[] { LocalAdminPermissions.PermissionUserManage });
            brokerDb.Update(managerSession);
            (await Request("GET", "/users", "/users", session: managerCookie)).Status.Should().Be(200);
            (await Request("GET", "/status", "/status", session: managerCookie)).Status.Should().Be(403);
            var managerOverview = await Request("GET", "/users/{userId}/overview", $"/users/{userId}/overview", userValues, session: managerCookie);
            managerOverview.Status.Should().Be(200);
            managerOverview.Data.GetProperty("last_turn").ValueKind.Should().Be(JsonValueKind.Null);
            managerOverview.Data.GetProperty("available_souls").ValueKind.Should().Be(JsonValueKind.Array);
            (await Request("GET", "/audit/traces/{traceId}", "/audit/traces/trace-admin", new() { ["traceId"] = "trace-admin" }, session: managerCookie)).Status.Should().Be(403);
            (await Request("GET", "/tools/availability", "/tools/availability", query: "?user_id=line%3Atest&channel_type=line", session: managerCookie)).Status.Should().Be(200);
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
    [InlineData("/users/user-1/notes")]
    [InlineData("/users/user-1/hide-references")]
    [InlineData("/users/web%3Auser%2F1/selections")]
    [InlineData("/users/user-1/turns")]
    [InlineData("/users/user-1/turns/turn-1/work")]
    [InlineData("/users/user-1/summaries")]
    public void UserReads_RequireUserManageOrAuditRead(string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization("GET", path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.PermissionUserManage, LocalAdminPermissions.AuditRead);
        superAdminOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("POST", "/users/user-1/notes")]
    [InlineData("POST", "/users/user-1/notes/note-1/restore")]
    [InlineData("POST", "/users/user-1/forget-find")]
    [InlineData("POST", "/users/user-1/forget")]
    [InlineData("POST", "/users/web%3Auser%2F1/hide-references/forget-1/unforget")]
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
    [InlineData("/audit/traces/nyat-1")]
    [InlineData("/users/user-1/audit")]
    public void AuditReads_RequireAuditRead(string path)
    {
        var resolved = NyaChatEndpoints.TryResolveRouteAuthorization("GET", path, out var permissions, out var superAdminOnly);

        resolved.Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.AuditRead);
        superAdminOnly.Should().BeFalse();
    }

    [Fact]
    public void ToolAvailability_RequiresStatusUserManageOrAuditRead()
    {
        NyaChatEndpoints.TryResolveRouteAuthorization("GET", "/tools/availability", out var permissions, out var onlySuperAdmin).Should().BeTrue();
        permissions.Should().Equal(LocalAdminPermissions.SystemStatusRead, LocalAdminPermissions.AuditRead, LocalAdminPermissions.PermissionUserManage);
        onlySuperAdmin.Should().BeFalse();
        NyaChatEndpoints.TryResolveRouteAuthorization("POST", "/tools/availability", out _, out _).Should().BeFalse();
        NyaChatEndpoints.TryResolveRouteAuthorization("DELETE", "/audit/traces/nyat-1", out _, out _).Should().BeFalse();
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
