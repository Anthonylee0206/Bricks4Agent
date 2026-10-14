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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace Unit.Tests.Admin;

public sealed class NyaChatAdminEndpointTests
{
    [Fact]
    public async Task AdminEndpoints_UseNyaDatabaseAndAuditIdentityAndRejectFailedWrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nya_admin_http_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var brokerDb = BrokerDb.UseSqlite($"Data Source={Path.Combine(directory, "broker.db")};Pooling=False");
            new BrokerDbInitializer(brokerDb).Initialize();
            using var nyaDb = new NyaDb(new NyaChatConfig { DbPath = Path.Combine(directory, "nya.db") + ";Pooling=False" },
                NullLogger<NyaDb>.Instance);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.Configuration["NyaChat:DataDirectory"] = Path.Combine(directory, "tools");
            builder.Services.AddSingleton(brokerDb);
            builder.Services.AddSingleton<LocalAdminAuthService>();
            var config = new NyaChatConfig
            {
                SoulsDirectory = Path.Combine(directory, "souls"),
                PromptsDirectory = Path.Combine(directory, "prompts"),
                DefaultSoulId = "custom_default",
                AuditEnabled = true
            };
            Directory.CreateDirectory(config.SoulsDirectory);
            builder.Services.AddNyaChat(config);
            builder.Services.AddSingleton(nyaDb);
            await using var app = builder.Build();
            NyaChatEndpoints.Map(app.MapGroup("/api/v1"));

            var auth = app.Services.GetRequiredService<LocalAdminAuthService>();
            var login = new DefaultHttpContext();
            login.Connection.RemoteIpAddress = IPAddress.Loopback;
            auth.Login(login, "admin", "admin", "admin-test-password").Authenticated.Should().BeTrue();
            var cookie = login.Response.Headers.SetCookie.Single()!.Split(';', 2)[0];
            var memory = app.Services.GetRequiredService<NyaMemoryService>();
            var souls = app.Services.GetRequiredService<NyaSoulProvider>();
            souls.SaveSoul(new SoulDefinition { SoulId = "custom_default", DisplayName = "Default" });
            souls.SaveSoul(new SoulDefinition { SoulId = "custom", DisplayName = "Custom" });
            memory.SetSoulBinding("binding-only", "CUSTOM");

            var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(ds => ds.Endpoints)
                .OfType<RouteEndpoint>().ToArray();
            async Task<(int Status, JsonDocument Body)> Request(string method, string template, string path,
                Dictionary<string, object?>? values = null, string? body = null, string? sessionCookie = null)
            {
                var endpoint = endpoints.Single(e => e.RoutePattern.RawText == "/api/v1/local-admin/nya" + template &&
                    e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));
                var ctx = new DefaultHttpContext { RequestServices = app.Services };
                ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
                ctx.Request.Method = method;
                ctx.Request.Path = "/api/v1/local-admin/nya" + path;
                ctx.Request.Headers.Cookie = sessionCookie ?? cookie;
                ctx.Request.RouteValues = new RouteValueDictionary(values ?? new());
                if (body != null) ctx.Items[EncryptionMiddleware.DecryptedBodyKey] = body;
                using var stream = new MemoryStream();
                ctx.Response.Body = stream;
                await endpoint.RequestDelegate!(ctx);
                stream.Position = 0;
                return (ctx.Response.StatusCode, await JsonDocument.ParseAsync(stream));
            }

            var preview = await Request("GET", "/souls/{soulId}", "/souls/custom", new() { ["soulId"] = "custom" });
            using (preview.Body)
            {
                preview.Status.Should().Be(200);
                preview.Body.RootElement.GetProperty("data").GetProperty("bound_users").EnumerateArray()
                    .Select(e => e.GetString()).Should().Equal("binding-only");
            }
            auth.CreateOperator("sys", "System Admin", LocalAdminRoles.SystemAdmin, "system-test-password", "local_admin");
            var systemLogin = new DefaultHttpContext();
            systemLogin.Connection.RemoteIpAddress = IPAddress.Loopback;
            auth.Login(systemLogin, "sys", "system-test-password", null).Authenticated.Should().BeTrue();
            var systemCookie = systemLogin.Response.Headers.SetCookie.Single()!.Split(';', 2)[0];
            var forbidden = await Request("DELETE", "/users/{userId}/soul", "/users/binding-only/soul",
                new() { ["userId"] = "binding-only" }, sessionCookie: systemCookie);
            using (forbidden.Body)
            {
                forbidden.Status.Should().Be(403);
                memory.GetSoulBinding("binding-only").Should().Be("CUSTOM");
            }
            var bind = await Request("POST", "/users/{userId}/soul", "/users/binding-only/soul",
                new() { ["userId"] = "binding-only" }, "{\"soul_id\":\"custom\"}");
            bind.Status.Should().Be(200);
            bind.Body.Dispose();
            var unbind = await Request("DELETE", "/users/{userId}/soul", "/users/binding-only/soul", new() { ["userId"] = "binding-only" });
            using (unbind.Body)
            {
                unbind.Status.Should().Be(200);
                var data = unbind.Body.RootElement.GetProperty("data");
                data.GetProperty("bound_soul_id").ValueKind.Should().Be(JsonValueKind.Null);
                data.GetProperty("effective_soul_id").GetString().Should().Be("custom_default");
            }
            var audit = app.Services.GetRequiredService<NyaAuditLogger>();
            audit.GetLogs("binding-only", "soul_bind").Logs.Should().ContainSingle();
            using var detail = JsonDocument.Parse(audit.GetLogs("binding-only", "soul_unbind").Logs.Single().DetailJson);
            detail.RootElement.GetProperty("operator").GetProperty("username").GetString().Should().Be("admin");
            detail.RootElement.GetProperty("operator").GetProperty("role").GetString().Should().Be(LocalAdminRoles.SuperAdmin);

            Directory.CreateDirectory(Path.Combine(directory, "tools", "nya_tools.json"));
            var toggle = await Request("POST", "/tools/{toolId}/toggle", "/tools/note_save/toggle",
                new() { ["toolId"] = "note_save" }, "{\"enabled\":false}");
            using (toggle.Body)
            {
                toggle.Status.Should().Be(500);
                toggle.Body.RootElement.GetProperty("success").GetBoolean().Should().BeFalse();
            }
            app.Services.GetRequiredService<NyaToolEnableStore>().Effective("note_save", true).Should().BeTrue();

            File.WriteAllText(Path.Combine(config.SoulsDirectory, "bad.json"), "{");
            var reload = await Request("POST", "/souls/reload", "/souls/reload");
            using (reload.Body)
            {
                var data = reload.Body.RootElement.GetProperty("data");
                data.GetProperty("ok").GetBoolean().Should().BeFalse();
                data.GetProperty("failures")[0].GetProperty("file").GetString().Should().Be("bad.json");
            }
            audit.GetLogs(action: "soul_reload", result: "error").Logs.Should().ContainSingle();
            var prompts = await Request("POST", "/prompts/reload", "/prompts/reload");
            prompts.Body.Dispose();
            audit.GetLogs(action: "prompt_reload").Logs.Should().ContainSingle();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
