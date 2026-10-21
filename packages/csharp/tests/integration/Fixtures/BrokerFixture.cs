using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Broker.Services;
using BrokerCore.Services;
using BrokerCore.Models;
using Xunit;

namespace Integration.Tests.Fixtures;

/// <summary>
/// Shared fixture that boots the Broker inside an in-memory TestServer
/// via WebApplicationFactory. Each test class that needs the running
/// Broker should implement IClassFixture&lt;BrokerFixture&gt;.
///
/// Key adaptations for the test environment:
///   - Environment set to "Testing" so production guards are skipped.
///   - Database path and high-level access root overridden (via UseSetting)
///     to per-run temp locations so tests never touch the real broker.db or
///     the local managed workspaces; both are removed on dispose.
///   - appsettings.json is loaded from the Broker project's content root.
/// </summary>
public class BrokerFixture : IAsyncLifetime
{
    private const string LineWorkerType = "line-worker";
    private const string LineWorkerKeyId = "line-v1";
    private const string LineWorkerSecret = "line-secret";
    private const string FileWorkerType = "file-worker";
    private const string FileWorkerKeyId = "file-v1";
    private const string FileWorkerSecret = "file-secret";
    private const int CleanupAttempts = 20;
    private static readonly TimeSpan CleanupDelay = TimeSpan.FromMilliseconds(250);
    private readonly string _tempDbPath;
    private readonly string _tempAccessRoot;
    private readonly WorkerIdentityAuthOptions _workerAuthOptions;

    public WebApplicationFactory<Program> Factory { get; }
    public HttpClient Client { get; private set; } = null!;
    public string DefaultLineUserId { get; } = "line-project-user";

    /// <summary>本次執行的受管工作區根目錄（暫存），供「回覆不含主機路徑」的斷言使用。</summary>
    public string AccessRoot => _tempAccessRoot;

    public BrokerFixture()
    {
        // Each test run gets its own isolated SQLite file
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"broker_integration_{Guid.NewGuid():N}.db");
        _tempAccessRoot = Path.Combine(Path.GetTempPath(), $"broker_integration_access_{Guid.NewGuid():N}");
        _workerAuthOptions = BuildWorkerAuthOptions();

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");

                // Program.cs 在建置 host 之前就讀取這幾個值，ConfigureAppConfiguration 到不了那個階段，
                // 必須用 UseSetting（同 BrokerAuthorizationFixture）；否則測試會共用 bin 下的 broker.db，
                // 並寫入本機真正的受管工作區。
                builder.UseSetting("Database:Path", _tempDbPath);
                builder.UseSetting("HighLevelCoordinator:AccessRoot", _tempAccessRoot);
                // 確認 draft 時不向本機模型詢問執行模型建議
                builder.UseSetting("HighLevelExecutionModelPolicy:Enabled", "false");
                // [修正 2026-10 AnthonyLee] 啟動時的法律條文 RAG 種子會從網路抓全文、逐條呼叫嵌入模型，
                // 每個測試主機（新資料庫）都要跑好幾分鐘，沒有 Ollama 時更會一直等逾時；整合測試都用不到它。
                builder.UseSetting("RagSeed:Enabled", "false");

                builder.ConfigureAppConfiguration((ctx, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        // Disable features that need external services
                        ["CacheCluster:Enabled"] = "false",
                        ["FunctionPool:Enabled"] = "false",
                        ["Embedding:Enabled"] = "false",
                        ["LineChatGateway:Enabled"] = "false",
                        ["HighLevelLlm:Enabled"] = "false",
                        ["LlmProxy:Enabled"] = "false",
                        ["WorkerAuth:Enforce"] = "true",
                        ["WorkerAuth:ClockSkewSeconds"] = _workerAuthOptions.ClockSkewSeconds.ToString(),
                        ["WorkerAuth:Credentials:0:WorkerType"] = LineWorkerType,
                        ["WorkerAuth:Credentials:0:KeyId"] = LineWorkerKeyId,
                        ["WorkerAuth:Credentials:0:SharedSecret"] = LineWorkerSecret,
                        ["WorkerAuth:Credentials:0:Status"] = "active",
                        ["WorkerAuth:Credentials:1:WorkerType"] = FileWorkerType,
                        ["WorkerAuth:Credentials:1:KeyId"] = FileWorkerKeyId,
                        ["WorkerAuth:Credentials:1:SharedSecret"] = FileWorkerSecret,
                        ["WorkerAuth:Credentials:1:Status"] = "active",
                        ["WorkerAuth:HttpRoutes:0:WorkerType"] = LineWorkerType,
                        ["WorkerAuth:HttpRoutes:0:Paths:0"] = "/api/v1/high-level/line/process",
                        ["WorkerAuth:HttpRoutes:0:Paths:1"] = "/api/v1/high-level/line/notifications/pending",
                        ["WorkerAuth:HttpRoutes:0:Paths:2"] = "/api/v1/high-level/line/notifications/complete",
                    });
                });
            });
    }

    public Task InitializeAsync()
    {
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false
        });
        return Task.CompletedTask;
    }

    public async Task<JsonDocument> SendHighLevelLineTextAsync(string message, string? userId = null)
        => await SendHighLevelLineTextAsWorkerAsync(LineWorkerType, LineWorkerKeyId, LineWorkerSecret, message, userId);

    public async Task<HttpResponseMessage> SendHighLevelLineTextUnsignedAsync(string message, string? userId = null)
    {
        return await Client.PostAsJsonAsync("/api/v1/high-level/line/process", new
        {
            user_id = userId ?? DefaultLineUserId,
            message
        });
    }

    public async Task<JsonDocument> SendHighLevelLineTextAsWorkerAsync(
        string workerType,
        string keyId,
        string sharedSecret,
        string message,
        string? userId = null)
    {
        var response = await SendHighLevelLineTextAsWorkerRawAsync(workerType, keyId, sharedSecret, message, userId);

        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    public Task<HttpResponseMessage> SendHighLevelLineTextAsWorkerRawAsync(
        string workerType,
        string keyId,
        string sharedSecret,
        string message,
        string? userId = null)
    {
        var payload = new
        {
            user_id = userId ?? DefaultLineUserId,
            message
        };
        return PostSignedAsync("/api/v1/high-level/line/process", payload, workerType, keyId, sharedSecret);
    }

    public Task<HttpResponseMessage> GetPendingNotificationsAsync()
        => GetSignedAsync("/api/v1/high-level/line/notifications/pending?limit=10", LineWorkerType, LineWorkerKeyId, LineWorkerSecret);

    public async Task EnableLineProductionAsync(string userId)
    {
        using var profileCreation = await SendHighLevelLineTextAsync("hello", userId);

        using var scope = Factory.Services.CreateScope();
        var coordinator = scope.ServiceProvider.GetRequiredService<HighLevelCoordinator>();
        coordinator.ReviewLineUserRegistration(userId, "member");
        coordinator.SetLineUserPermissions(userId, new HighLevelUserPermissionsPatch
        {
            AllowProduction = true
        });
    }

    private async Task<HttpResponseMessage> PostSignedAsync<TBody>(string path, TBody payload, string workerType, string keyId, string sharedSecret)
    {
        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
        AddWorkerHeaders(request, HttpMethod.Post.Method, path, json, workerType, keyId, sharedSecret);
        return await Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> GetSignedAsync(string pathAndQuery, string workerType, string keyId, string sharedSecret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        AddWorkerHeaders(request, HttpMethod.Get.Method, pathAndQuery.Split('?')[0], string.Empty, workerType, keyId, sharedSecret);
        return await Client.SendAsync(request);
    }

    private void AddWorkerHeaders(HttpRequestMessage request, string method, string path, string body, string workerType, string keyId, string sharedSecret)
    {
        var service = new WorkerIdentityAuthService(_workerAuthOptions, new WorkerAuthNonceStore());
        var timestamp = DateTimeOffset.UtcNow;
        var nonce = Guid.NewGuid().ToString("N");
        var signature = service.SignHttp(workerType, keyId, sharedSecret, method, path, body, timestamp, nonce);
        request.Headers.Add("X-B4A-Worker-Type", workerType);
        request.Headers.Add("X-B4A-Key-Id", keyId);
        request.Headers.Add("X-B4A-Timestamp", timestamp.ToString("O"));
        request.Headers.Add("X-B4A-Nonce", nonce);
        request.Headers.Add("X-B4A-Signature", signature);
    }

    private static WorkerIdentityAuthOptions BuildWorkerAuthOptions() => new()
    {
        ClockSkewSeconds = 300,
        Credentials =
        [
            new WorkerCredentialRecord { WorkerType = LineWorkerType, KeyId = LineWorkerKeyId, SharedSecret = LineWorkerSecret, Status = "active" },
            new WorkerCredentialRecord { WorkerType = FileWorkerType, KeyId = FileWorkerKeyId, SharedSecret = FileWorkerSecret, Status = "active" }
        ],
        HttpRoutes =
        [
            new WorkerRouteRule
            {
                WorkerType = LineWorkerType,
                Paths =
                [
                    "/api/v1/high-level/line/process",
                    "/api/v1/high-level/line/notifications/pending",
                    "/api/v1/high-level/line/notifications/complete"
                ]
            }
        ]
    };

    public async Task<ProjectInterviewTaskDocument> ReadProjectInterviewRequirementsAsync(string channel, string userId)
    {
        await Task.Yield();
        using var scope = Factory.Services.CreateScope();
        var service = new ProjectInterviewStateService(scope.ServiceProvider.GetRequiredService<BrokerCore.Data.BrokerDb>());
        return await service.LoadTaskDocumentAsync(channel, userId, CancellationToken.None);
    }

    public async Task<JsonDocument> CompleteProjectInterviewToReviewAsync(string? userId = null)
    {
        var resolvedUserId = userId ?? DefaultLineUserId;
        var uniqueProjectName = "#AlphaPortal" + Guid.NewGuid().ToString("N");
        // /proj 需要 AllowProduction（會員層級）
        await EnableLineProductionAsync(resolvedUserId);
        await SendHighLevelLineTextAsync("/proj", resolvedUserId);
        await SendHighLevelLineTextAsync(uniqueProjectName, resolvedUserId);
        await SendHighLevelLineTextAsync("2", resolvedUserId);
        return await SendHighLevelLineTextAsync("3", resolvedUserId);
    }

    public Task<ProjectInterviewTaskDocument> ReadProjectInterviewReviewAsync(string channel, string userId)
        => ReadProjectInterviewRequirementsAsync(channel, userId);

    public Task<ProjectInterviewVersionDag?> ReadProjectInterviewVersionDagAsync(string channel, string userId, int version)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerCore.Data.BrokerDb>();
        var entry = db.Query<SharedContextEntry>(
            "SELECT * FROM shared_context_entries WHERE document_id = @docId ORDER BY version DESC LIMIT 1",
            new { docId = ProjectInterviewStateService.BuildVersionDagDocumentId(channel, userId, version) })
            .FirstOrDefault();

        if (entry == null || string.IsNullOrWhiteSpace(entry.ContentRef))
            return Task.FromResult<ProjectInterviewVersionDag?>(null);

        return Task.FromResult(JsonSerializer.Deserialize<ProjectInterviewVersionDag>(entry.ContentRef));
    }

    public Task AgeProjectInterviewRequirementsAsync(string channel, string userId, TimeSpan age)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BrokerCore.Data.BrokerDb>();
        var documentId = ProjectInterviewStateService.BuildTaskDocumentId(channel, userId);
        var staleCreatedAt = DateTime.UtcNow.Subtract(age);
        db.Execute(
            """
            UPDATE shared_context_entries
            SET created_at = @createdAt
            WHERE document_id = @documentId
              AND version = (
                  SELECT MAX(version)
                  FROM shared_context_entries
                  WHERE document_id = @documentId
              )
            """,
            new
            {
                createdAt = staleCreatedAt,
                documentId
            });

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();

        // broker 使用 SQLite 連線池；先釋放連線，暫存資料庫檔才刪得掉。
        SqliteConnection.ClearAllPools();
        for (var attempt = 1; attempt <= CleanupAttempts; attempt++)
        {
            if (TryDeleteTempArtifacts())
                return;

            SqliteConnection.ClearAllPools();
            await Task.Delay(CleanupDelay);
        }
    }

    private bool TryDeleteTempArtifacts()
    {
        try
        {
            foreach (var suffix in new[] { "", "-shm", "-wal" })
            {
                var file = _tempDbPath + suffix;
                if (File.Exists(file))
                    File.Delete(file);
            }

            if (Directory.Exists(_tempAccessRoot))
                Directory.Delete(_tempAccessRoot, recursive: true);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
