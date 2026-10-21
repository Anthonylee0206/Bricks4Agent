using System.Net;
using System.Text.Json;
using Integration.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using WorkerSdk;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Integration.Tests.Api;

/// <summary>
/// 開啟 Function Pool 的 broker:健康端點在真的有 worker 連上時回報正確內容。
///
/// 其他整合測試的主機都沒有開 Function Pool,所以 /health/* 只被測過「匿名不能拿到結果」,
/// 從來沒有被測過「開起來之後到底回什麼」。這裡用 worker-sdk 起一個真的 WorkerHost,
/// 經 TCP 連到 broker 的 pool,再以管理員身分讀健康端點。
/// </summary>
public sealed class HealthEndpointsPoolTests : IClassFixture<FunctionPoolEnabledFixture>
{
    private const string AdminRole = "role_admin";
    private const string ReaderRole = "role_reader";
    private const string ProbeCapability = "health.probe.test";

    private readonly FunctionPoolEnabledFixture _fixture;
    private readonly EncryptedBrokerClient _client;

    private readonly ITestOutputHelper _out;

    public HealthEndpointsPoolTests(FunctionPoolEnabledFixture fixture, ITestOutputHelper output)
    {
        _out = output;
        _fixture = fixture;
        _client = new EncryptedBrokerClient(fixture.Client, fixture.Services);
    }

    [Fact]
    public async Task WorkersAndScore_ReflectARealWorker_FromNoneToReady()
    {
        var admin = _client.OpenSession(AdminRole);

        // 還沒有 worker:不是「健康」。
        var before = await GetDataAsync("/api/v1/health/score", admin.ScopedToken);
        before.GetProperty("worker_count").GetInt32().Should().Be(0);
        before.GetProperty("overall_status").GetString().Should().Be("no_workers");
        before.GetProperty("overall_score").GetInt32().Should().Be(0);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var host = new WorkerHost(new WorkerHostOptions
        {
            BrokerHost = "127.0.0.1",
            BrokerPort = _fixture.PoolPort,
            WorkerType = "health-probe-test",
            HeartbeatIntervalSeconds = 1,
            AutoReconnect = false,
            // broker 在檢查 WorkerAuth:Enforce 之前就要求註冊訊息帶齊認證欄位;不強制時不驗內容,
            // 但欄位不能是空的(見 function-pool/Network/WorkerSession.cs 的註冊處理)。
            WorkerAuthKeyId = "health-probe-test-key",
            WorkerAuthSharedSecret = "health-probe-test-secret",
        }, new OutLogger(_out));
        host.RegisterHandler(new ProbeHandler());
        var run = host.RunAsync(cts.Token);

        try
        {
            var workers = await WaitForAsync(
                "/api/v1/health/workers", admin.ScopedToken,
                data => data.GetProperty("worker_count").GetInt32() == 1);

            var w = workers.GetProperty("workers")[0];
            w.GetProperty("status").GetString().Should().Be("ready");
            w.GetProperty("available").GetBoolean().Should().BeTrue();
            w.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString())
                .Should().Contain(ProbeCapability);
            w.GetProperty("heartbeat_age_seconds").GetInt32().Should().BeLessThan(15);
            workers.GetProperty("available_count").GetInt32().Should().Be(1);

            var score = await GetDataAsync("/api/v1/health/score", admin.ScopedToken);
            score.GetProperty("worker_count").GetInt32().Should().Be(1);
            score.GetProperty("overall_status").GetString().Should().Be("healthy");
            var heartbeat = score.GetProperty("workers")[0].GetProperty("heartbeat");
            heartbeat.GetProperty("label").GetString().Should().Be("fresh");
        }
        finally
        {
            cts.Cancel();
            host.Stop();
            try { await run; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task LlmMetrics_AreReadableByAdmin()
    {
        var admin = _client.OpenSession(AdminRole);

        var data = await GetDataAsync("/api/v1/health/llm?bucket_minutes=5&buckets=6", admin.ScopedToken);

        data.GetProperty("total_calls").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        data.TryGetProperty("per_model", out _).Should().BeTrue();
        data.GetProperty("bucket_minutes").GetInt32().Should().Be(5);
        data.GetProperty("trend").GetArrayLength().Should().Be(6);
    }

    [Theory]
    [InlineData("/api/v1/health/workers")]
    [InlineData("/api/v1/health/score")]
    [InlineData("/api/v1/health/llm")]
    public async Task HealthEndpoints_RejectNonAdmin_EvenWhenPoolIsEnabled(string path)
    {
        var reader = _client.OpenSession(ReaderRole);

        var reply = await _client.SendPlainAsync("GET", path, bearerToken: reader.ScopedToken);

        reply.Status.Should().BeOneOf(new[] { 401, 403 }, "the response was {0}", reply);
    }

    private async Task<JsonElement> GetDataAsync(string path, string token)
    {
        var reply = await _client.SendPlainAsync("GET", path, bearerToken: token);
        reply.StatusCode.Should().Be(HttpStatusCode.OK, "the response was {0}", reply);
        using var doc = JsonDocument.Parse(reply.Body);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task<JsonElement> WaitForAsync(string path, string token, Func<JsonElement, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonElement last;
        do
        {
            last = await GetDataAsync(path, token);
            if (condition(last)) return last;
            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);

        throw new Xunit.Sdk.XunitException($"Condition not met for {path}; last data: {last}");
    }

    private sealed class OutLogger(ITestOutputHelper o) : ILogger<WorkerHost>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f)
        { try { o.WriteLine($"[{l}] {f(s, ex)} {ex?.GetType().Name}: {ex?.Message}"); } catch { } }
    }

    private sealed class ProbeHandler : ICapabilityHandler
    {
        public string CapabilityId => ProbeCapability;

        public Task<(bool Success, string? ResultPayload, string? Error)> ExecuteAsync(
            string requestId, string route, string payload, string scope, CancellationToken ct)
            => Task.FromResult<(bool, string?, string?)>((true, "{}", null));
    }
}
