using System.Net;
using System.Text;
using System.Text.Json;
using Broker.Endpoints;
using Broker.Middleware;
using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using Broker.Services;
using BrokerCore.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Unit.Tests.Admin;

public sealed class NyaChatSettingsTests
{
    [Theory]
    [InlineData("GET", "/config/effective", false)]
    [InlineData("PATCH", "/config", false)]
    [InlineData("POST", "/config/reset", false)]
    [InlineData("GET", "/llm/profiles/main/effective", false)]
    [InlineData("POST", "/llm/profiles/main/test", true)]
    public void NewRoutes_HaveExplicitAuthorization(string method, string path, bool superAdmin)
    {
        NyaChatEndpoints.TryResolveRouteAuthorization(method, path, out var permissions, out var onlySuperAdmin).Should().BeTrue();
        onlySuperAdmin.Should().Be(superAdmin);
        if (superAdmin) permissions.Should().BeEmpty();
        else if (method != "GET") permissions.Should().Equal(LocalAdminPermissions.SystemWorkerManage);
    }

    [Fact]
    public async Task Config_ResetPreservesHiddenEngineOverride()
    {
        await using var host = new Host();
        var db = host.App.Services.GetRequiredService<NyaDb>().Db;
        db.EnsureTable<NyaConfigEntry>();
        db.Insert(new NyaConfigEntry { ConfigKey = "nya:chat", ConfigJson = "{\"Enabled\":false,\"ShortTermMessageLimit\":1200}" });
        var store = new NyaChatConfigStore(new NyaChatConfig { Enabled = true, ShortTermMessageLimit = 200 }, db, NullLogger<NyaChatConfigStore>.Instance);
        store.Current.Enabled.Should().BeFalse();
        await store.ResetAsync("admin", "super_admin", default);
        store.Current.Enabled.Should().BeFalse();
        store.Current.ShortTermMessageLimit.Should().Be(200);
        db.Get<NyaConfigEntry>("nya:chat")!.ConfigJson.Should().Be("{\"Enabled\":false}");
        var restarted = new NyaChatConfigStore(new NyaChatConfig { Enabled = true }, db, NullLogger<NyaChatConfigStore>.Instance);
        restarted.Current.Enabled.Should().BeFalse();
    }

    [Fact]
    public async Task Config_PatchesPersistPartialOverridesAndResetWithAuditEvenWhenDisabled()
    {
        await using var host = new Host();
        var store = (NyaChatConfigStore)host.App.Services.GetRequiredService<INyaConfigStore<NyaChatConfig>>();
        var response = await host.Request("PATCH", "/config", body: "{\"ShortTermMessageLimit\":1200,\"AuditEnabled\":false}");
        response.Status.Should().Be(200);
        store.Current.ShortTermMessageLimit.Should().Be(1200);
        store.Current.MaxToolRounds.Should().Be(3);
        var effective = await host.Request("GET", "/config/effective");
        var settings = effective.Data.GetProperty("settings").EnumerateArray().ToDictionary(s => s.GetProperty("key").GetString()!);
        settings["ShortTermMessageLimit"].GetProperty("value").GetInt32().Should().Be(1200);
        settings.Keys.Should().NotContain(k => k.StartsWith("Summar") || k == "AutoSummarizeEnabled");
        effective.Data.TryGetProperty("read_only", out _).Should().BeFalse();
        var nyaDb = host.App.Services.GetRequiredService<NyaDb>().Db;
        nyaDb.Get<NyaConfigEntry>("nya:chat")!.ConfigJson.Should().NotContain("ApiKey").And.NotContain("DbPath");
        nyaDb.GetAll<NyaAuditLog>().Should().ContainSingle(l => l.Action == "config_update" && l.DetailJson.Contains("admin"));
        var restart = new NyaChatConfigStore(new NyaChatConfig { ShortTermMessageLimit = 300, MaxToolRounds = 5 }, nyaDb, NullLogger<NyaChatConfigStore>.Instance);
        restart.Current.ShortTermMessageLimit.Should().Be(1200);
        restart.Current.MaxToolRounds.Should().Be(5);
        var reset = await host.Request("POST", "/config/reset");
        reset.Status.Should().Be(200);
        store.Current.ShortTermMessageLimit.Should().Be(200);
        nyaDb.Get<NyaConfigEntry>("nya:chat").Should().BeNull();
        nyaDb.GetAll<NyaAuditLog>().Should().ContainSingle(l => l.Action == "config_reset");
        var config = await host.Request("GET", "/config");
        config.Root.GetRawText().Should().NotContain("legacy-secret-value").And.NotContain("nested-secret-value");
        store.Current.ChatApiKey.Should().Be("legacy-secret-value");
        (await host.Request("GET", "/config/effective")).Root.GetRawText().Should().NotContain("secret-value");
    }

    [Theory]
    [InlineData("{\"DbPath\":\"other.db\"}")]
    [InlineData("{\"TokenEstimatorCjkPerChar\":2}")]
    [InlineData("{\"LlmProfiles\":{}}")]
    [InlineData("{\"ChatApiKey\":\"secret\"}")]
    [InlineData("{\"TaskRouting\":{}}")]
    [InlineData("{\"ShortTermMessageLimit\":0}")]
    [InlineData("{\"ShortTermMessageLimit\":1.5}")]
    [InlineData("{\"OutputReserveRatio\":1.1}")]
    [InlineData("{\"MaxToolRounds\":21}")]
    [InlineData("{\"AuditEnabled\":\"false\"}")]
    [InlineData("{\"DefaultSoulId\":\"../evil\"}")]
    [InlineData("{\"MinOutputTokens\":4096}")]
    [InlineData("{\"DefaultMaxContextTokens\":512}")]
    [InlineData("{\"SummarizeKeepRecentCount\":12}")]
    [InlineData("{\"maxToolRounds\":4,\"MaxToolRounds\":5}")]
    [InlineData("{}")]
    [InlineData("[]")]

    public async Task Config_RejectsUnsafeOrIgnoredFieldsWithoutMutation(string body)
    {
        await using var host = new Host();
        (await host.Request("PATCH", "/config", body: body)).Status.Should().Be(400);
        (await host.Request("PUT", "/config", body: body)).Status.Should().Be(400);
        host.App.Services.GetRequiredService<NyaDb>().Db.Get<NyaConfigEntry>("nya:chat").Should().BeNull();
    }

    [Fact]
    public async Task Config_FailedAuditRollsBackPersistenceAndConcurrentPatchesMerge()
    {
        await using var host = new Host();
        var store = (NyaChatConfigStore)host.App.Services.GetRequiredService<INyaConfigStore<NyaChatConfig>>();
        using var a = JsonDocument.Parse("{\"ShortTermMessageLimit\":400}");
        using var b = JsonDocument.Parse("{\"MaxToolRounds\":5}");
        await Task.WhenAll(Task.Run(() => store.PatchAsync(a.RootElement, "a", "super_admin", default)),
            Task.Run(() => store.PatchAsync(b.RootElement, "b", "super_admin", default)));
        store.Current.ShortTermMessageLimit.Should().Be(400);
        store.Current.MaxToolRounds.Should().Be(5);
        var db = host.App.Services.GetRequiredService<NyaDb>().Db;
        db.Execute("DROP TABLE nya_audit_log");
        using var invalidAudit = JsonDocument.Parse("{\"MaxToolRounds\":7}");
        Func<Task> patch = () => store.PatchAsync(invalidAudit.RootElement, "admin", "super_admin", default);
        await patch.Should().ThrowAsync<Exception>();
        store.Current.MaxToolRounds.Should().Be(5);
        db.Get<NyaConfigEntry>("nya:chat")!.ConfigJson.Should().Contain("5").And.NotContain("7");
    }

    [Fact]
    public async Task Config_DefaultSoulMustExistAndSystemAdminCanChangeHotSettings()
    {
        await using var host = new Host();
        (await host.Request("PATCH", "/config", body: "{\"DefaultSoulId\":\"missing\"}", system: true)).Status.Should().Be(400);
        var souls = host.App.Services.GetRequiredService<NyaSoulProvider>();
        souls.SaveSoul(new SoulDefinition { SoulId = "new_default", DisplayName = "New Default" });
        (await host.Request("PATCH", "/config", body: "{\"DefaultSoulId\":\"new_default\"}", system: true)).Status.Should().Be(200);
        souls.ResolveSoul("unbound-user").Soul.SoulId.Should().Be("new_default");
        (await host.Request("POST", "/config/reset", system: true)).Status.Should().Be(200);
    }

    [Fact]
    public async Task Llm_UsesOnlySupportedCallSitesAndShowsFallbackUsageAndDeleteEffects()
    {
        await using var host = new Host();
        var sites = await host.Request("GET", "/llm/call-sites");
        sites.Data.GetProperty("call_sites").EnumerateArray().Select(s => s.GetString()).Should()
            .Equal("chat", "summarization", "__default__");
        (await host.Request("PUT", "/llm/routes", body: "{\"call_site_id\":\"tool:search\",\"profile_id\":\"chat\"}")).Status.Should().Be(400);
        (await host.Request("PUT", "/llm/routes", body: "{\"call_site_id\":\"typo\",\"profile_id\":\"__default__\"}")).Status.Should().Be(400);
        (await host.Request("PUT", "/llm/routes", body: "{\"call_site_id\":\"summarization\",\"profile_id\":\"__default__\"}")).Status.Should().Be(200);
        var routes = await host.Request("GET", "/llm/routes");
        var extraction = routes.Data.EnumerateArray().Single(r => r.GetProperty("call_site_id").GetString() == "summarization");
        extraction.GetProperty("effective_profile_id").GetString().Should().Be("chat");
        extraction.GetProperty("source").GetString().Should().Be("__default__");
        var profiles = await host.Request("GET", "/llm/profiles");
        profiles.Data.EnumerateArray().Single(p => p.GetProperty("profile_id").GetString() == "chat")
            .GetProperty("call_sites").EnumerateArray().Select(s => s.GetString()).Should().Contain("summarization");
        (await host.Request("POST", "/llm/profiles", body: ProfileBody("custom"))).Status.Should().Be(200);
        (await host.Request("PUT", "/llm/routes", body: "{\"call_site_id\":\"summarization\",\"profile_id\":\"custom\"}")).Status.Should().Be(200);
        (await host.Request("DELETE", "/llm/profiles/{id}", id: "custom")).Status.Should().Be(200);
        host.App.Services.GetRequiredService<NyaLlmProfileStore>().ResolveProfile("summarization").Model.Should().Be("seed-model");
        (await host.Request("DELETE", "/llm/profiles/{id}", id: "chat")).Status.Should().Be(409);
    }

    [Fact]
    public async Task Profiles_EnforceCrudSemanticsPreserveMaskedKeyAndSendEffectiveSamplingThroughRealProvider()
    {
        await using var host = new Host();
        var payload = ProfileBody("main", "{\"temperature\":0.7,\"top_p\":0.8,\"presence_penalty\":0.4,\"model_overrides\":{\"temperature\":0.2,\"presence_penalty\":1.5,\"top_k\":20}}");
        (await host.Request("POST", "/llm/profiles", body: payload)).Status.Should().Be(200);
        (await host.Request("POST", "/llm/profiles", body: ProfileBody("main"))).Status.Should().Be(409);
        (await host.Request("PUT", "/llm/profiles/{id}", id: "missing", body: payload)).Status.Should().Be(404);
        (await host.Request("PUT", "/llm/profiles/{id}", id: "main", body: payload.Replace("profile-secret-value", "****"))).Status.Should().Be(200);
        host.App.Services.GetRequiredService<NyaLlmProfileStore>().GetProfile("main")!.ApiKey.Should().Be("profile-secret-value");
        var effective = await host.Request("GET", "/llm/profiles/{id}/effective", id: "main");
        effective.Data.GetProperty("sampling").GetProperty("temperature").GetDouble().Should().Be(0.2);
        var denied = await host.Request("POST", "/llm/profiles/{id}/test", id: "main", system: true);
        denied.Status.Should().Be(403);
        host.Http.Calls.Should().Be(0);
        var test = await host.Request("POST", "/llm/profiles/{id}/test", id: "main");
        test.Status.Should().Be(200);
        test.Data.GetProperty("ok").GetBoolean().Should().BeTrue();
        test.Data.GetProperty("latency_ms").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        host.Http.Endpoint.Should().Be("http://localhost:1234/v1/chat/completions");
        host.Http.Authorization.Should().Be("Bearer profile-secret-value");
        using var wire = JsonDocument.Parse(host.Http.Body!);
        foreach (var param in effective.Data.GetProperty("sampling").EnumerateObject())
            wire.RootElement.GetProperty(param.Name).GetDouble().Should().Be(param.Value.GetDouble());
        wire.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(32);
        wire.RootElement.GetProperty("model").GetString().Should().Be("test-model");
        host.Http.Status = HttpStatusCode.BadRequest;
        var failure = await host.Request("POST", "/llm/profiles/{id}/test", id: "main");
        failure.Data.GetProperty("ok").GetBoolean().Should().BeFalse();
        failure.Data.GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        failure.Root.GetRawText().Should().NotContain("profile-secret-value");
        (await host.Request("POST", "/llm/profiles/{id}/test", id: "missing")).Status.Should().Be(404);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"temperature\":3}")]
    [InlineData("{\"temperature\":\"0.2\"}")]
    [InlineData("{\"top_p\":-1}")]
    [InlineData("{\"presence_penalty\":3}")]
    [InlineData("{\"frequency_penalty\":-3}")]
    [InlineData("{\"repeat_penalty\":3}")]
    [InlineData("{\"enable_thinking\":1}")]
    [InlineData("{\"thinking_budget\":1.5}")]
    [InlineData("{\"timeout_seconds\":0}")]
    [InlineData("{\"max_context_tokens\":0}")]
    [InlineData("{\"unknown\":1}")]
    [InlineData("{\"model_overrides\":{\"custom\":\"bad\"}}")]
    [InlineData("{\"temperature\":0.2,\"temperature\":0.3}")]
    [InlineData("{\"extra_body\":[]}")]
    [InlineData("{\"extra_body\":{\"model\":\"other\"}}")]
    [InlineData("{\"extra_body\":{\"messages\":[]}}")]
    [InlineData("{\"extra_body\":{\"tools\":[]}}")]
    [InlineData("{\"extra_body\":{\"stream\":true}}")]
    public async Task Profiles_RejectInvalidParams(string parameters)
    {
        await using var host = new Host();
        (await host.Request("POST", "/llm/profiles", body: ProfileBody("bad", parameters))).Status.Should().Be(400);
        host.App.Services.GetRequiredService<NyaLlmProfileStore>().GetProfile("bad").Should().BeNull();
    }

    [Theory]
    [InlineData("unknown", "http://localhost:1234/v1/chat/completions", "model")]
    [InlineData("openai_chat", "http://localhost:1234", "model")]
    [InlineData("openai_chat", "http://localhost:1234/", "model")]
    [InlineData("openai_chat", "ftp://localhost/api/chat", "model")]
    [InlineData("openai_chat", "/v1/chat/completions", "model")]
    [InlineData("openai_chat", "http://localhost:1234/v1/chat/completions", "  ")]
    public async Task Profiles_RejectBadTransport(string provider, string url, string model)
    {
        await using var host = new Host();
        var body = JsonSerializer.Serialize(new { profile_id = "bad", provider, base_url = url, model, params_json = "{}" });
        (await host.Request("POST", "/llm/profiles", body: body)).Status.Should().Be(400);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"model\":\"other\"}")]
    [InlineData("{\"messages\":[]}")]
    [InlineData("{\"tools\":[]}")]
    [InlineData("{\"stream\":true}")]
    public async Task Profiles_RejectInvalidExtraBodyField(string extra)
    {
        await using var host = new Host();
        var body = System.Text.Json.Nodes.JsonNode.Parse(ProfileBody("bad-extra"))!.AsObject();
        body["extra_body"] = System.Text.Json.Nodes.JsonNode.Parse(extra);
        (await host.Request("POST", "/llm/profiles", body: body.ToJsonString())).Status.Should().Be(400);
        host.App.Services.GetRequiredService<NyaLlmProfileStore>().GetProfile("bad-extra").Should().BeNull();
    }

    [Fact]
    public async Task Profiles_PersistAndExposeExtraBodyFieldAndUseItForTheSavedProfileRequest()
    {
        await using var host = new Host();
        var body = System.Text.Json.Nodes.JsonNode.Parse(ProfileBody("extra"))!.AsObject();
        body["extra_body"] = System.Text.Json.Nodes.JsonNode.Parse("{\"thinking\":{\"type\":\"disabled\"}}");
        (await host.Request("POST", "/llm/profiles", body: body.ToJsonString())).Status.Should().Be(200);
        var effective = await host.Request("GET", "/llm/profiles/{id}/effective", id: "extra");
        effective.Data.GetProperty("extra_body").GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
        var listed = await host.Request("GET", "/llm/profiles");
        listed.Data.EnumerateArray().Single(p => p.GetProperty("profile_id").GetString() == "extra").GetProperty("extra_body").GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
        (await host.Request("POST", "/llm/profiles/{id}/test", id: "extra")).Status.Should().Be(200);
        using var wire = JsonDocument.Parse(host.Http.Body!);
        wire.RootElement.GetProperty("thinking").GetProperty("type").GetString().Should().Be("disabled");
    }

    private static string ProfileBody(string id, string parameters = "{}") => JsonSerializer.Serialize(new
    {
        profile_id = id, provider = "openai_chat", base_url = "http://localhost:1234/v1/chat/completions",
        api_key = "profile-secret-value", model = "test-model", params_json = parameters
    });

    [Theory]
    [InlineData("openai_chat", "body")]
    [InlineData("openai_responses", "body")]
    [InlineData("anthropic", "body")]
    [InlineData("gemini", "generationConfig")]
    [InlineData("ollama", "options")]
    public async Task Profiles_EffectiveSamplingMatchesEachRealProviderWire(string provider, string location)
    {
        await using var host = new Host();
        var budgetKey = provider switch { "ollama" => "num_predict", "gemini" => "maxOutputTokens", "openai_responses" => "max_output_tokens", _ => "max_tokens" };
        var json = JsonSerializer.Serialize(new { temperature = 0.7, top_p = 0.8,
            model_overrides = new Dictionary<string, double> { ["temperature"] = 0.2, ["custom_sampling"] = 20, [budgetKey] = 10000 } });
        var payload = ProfileBody("probe", json).Replace("openai_chat", provider);
        (await host.Request("POST", "/llm/profiles", body: payload)).Status.Should().Be(200);
        var effective = await host.Request("GET", "/llm/profiles/{id}/effective", id: "probe");
        effective.Data.GetProperty("sampling_location").GetString().Should().Be(location);
        effective.Data.GetProperty("sampling").TryGetProperty(budgetKey, out _).Should().BeFalse();
        await host.Request("POST", "/llm/profiles/{id}/test", id: "probe");
        host.Http.Calls.Should().Be(1);
        using var wire = JsonDocument.Parse(host.Http.Body!);
        var sent = location == "body" ? wire.RootElement : wire.RootElement.GetProperty(location);
        foreach (var param in effective.Data.GetProperty("sampling").EnumerateObject())
            sent.GetProperty(param.Name).GetDouble().Should().Be(param.Value.GetDouble());
        if (provider == "openai_responses")
        {
            effective.Data.GetProperty("sampling").EnumerateObject().Should().BeEmpty();
            sent.TryGetProperty("temperature", out _).Should().BeFalse();
            sent.GetProperty(budgetKey).GetInt32().Should().Be(32);
        }
        else
            sent.GetProperty(budgetKey).GetInt32().Should().Be(32);
    }

    private sealed class RecordingHttp : HttpMessageHandler, IHttpClientFactory
    {
        public int Calls { get; private set; }
        public string? Endpoint { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Endpoint = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(Status) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"OK\"}}]}", Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Host : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"nya_settings_{Guid.NewGuid():N}");
        private readonly BrokerDb _brokerDb;
        private readonly string _cookie;
        private readonly string _systemCookie;
        public WebApplication App { get; }
        public RecordingHttp Http { get; } = new();

        public Host()
        {
            Directory.CreateDirectory(_directory);
            _brokerDb = BrokerDb.UseSqlite($"Data Source={Path.Combine(_directory, "broker.db")};Pooling=False");
            new BrokerDbInitializer(_brokerDb).Initialize();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.Logging.ClearProviders();
            builder.Configuration["NyaChat:DataDirectory"] = Path.Combine(_directory, "tools");
            builder.Services.AddSingleton(_brokerDb);
            builder.Services.AddSingleton<LocalAdminAuthService>();
            builder.Services.AddNyaChat(new NyaChatConfig
            {
                DbPath = Path.Combine(_directory, "nya.db") + ";Pooling=False", SoulsDirectory = Path.Combine(_directory, "souls"),
                PromptsDirectory = Path.Combine(_directory, "prompts"), ChatApiKey = "legacy-secret-value", ChatModel = "seed-model",
                LlmProfiles = new() { ["nested"] = new NyaLlmProfile { ApiKey = "nested-secret-value" } }
            });
            builder.Services.AddSingleton<IHttpClientFactory>(Http);
            App = builder.Build();
            NyaChatEndpoints.Map(App.MapGroup("/api/v1"));
            var auth = App.Services.GetRequiredService<LocalAdminAuthService>();
            _cookie = Login(auth, "admin", "admin", "admin-test-password");
            auth.CreateOperator("sys", "System Admin", LocalAdminRoles.SystemAdmin, "system-test-password", "local_admin");
            _systemCookie = Login(auth, "sys", "system-test-password", null);
        }

        private static string Login(LocalAdminAuthService auth, string username, string password, string? newPassword)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            auth.Login(ctx, username, password, newPassword).Authenticated.Should().BeTrue();
            return ctx.Response.Headers.SetCookie.Single()!.Split(';', 2)[0];
        }

        public async Task<(int Status, JsonElement Root, JsonElement Data)> Request(string method, string template,
            string? body = null, string? id = null, bool system = false)
        {
            var endpoint = ((IEndpointRouteBuilder)App).DataSources.SelectMany(ds => ds.Endpoints).OfType<RouteEndpoint>()
                .Single(e => e.RoutePattern.RawText == "/api/v1/local-admin/nya" + template && e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));
            var ctx = new DefaultHttpContext { RequestServices = App.Services };
            ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
            ctx.Request.Method = method;
            ctx.Request.Path = "/api/v1/local-admin/nya" + template.Replace("{id}", id ?? "");
            ctx.Request.Headers.Cookie = system ? _systemCookie : _cookie;
            if (id != null) ctx.Request.RouteValues["id"] = id;
            if (body != null) ctx.Items[EncryptionMiddleware.DecryptedBodyKey] = body;
            using var stream = new MemoryStream();
            ctx.Response.Body = stream;
            await endpoint.RequestDelegate!(ctx);
            stream.Position = 0;
            using var doc = await JsonDocument.ParseAsync(stream);
            var root = doc.RootElement.Clone();
            return (ctx.Response.StatusCode, root, root.TryGetProperty("data", out var data) ? data : default);
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            _brokerDb.Dispose();
            Http.Dispose();
            Directory.Delete(_directory, true);
        }
    }
}
