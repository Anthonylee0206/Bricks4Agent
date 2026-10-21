using BrokerCore.Crypto;
using BrokerCore.Data;
using BrokerCore.Services;
using Broker.Adapters;
using Broker.Endpoints;
using Broker.Middleware;
using Broker.Services;
using FunctionPool.Container;
using FunctionPool.Dispatch;
using FunctionPool.Health;
using FunctionPool.Models;
using FunctionPool.Network;
using FunctionPool.Registry;
using Microsoft.Extensions.FileProviders;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

// ── 共用 LoggerFactory（修復 M-7：消除冗餘 LoggerFactory 實例） ──
using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var startupLogger = startupLoggerFactory.CreateLogger("Program");

// ── 密鑰啟動驗證（必須在讀取 ScopedToken／Encryption／WorkerAuth／ArtifactDownload 設定之前） ──
// 已外洩值任何環境都拒絕；佔位值只在 Development／Testing（或 Broker:AllowEphemeralKeys=true）改用隨機金鑰。
// 下方 ECDH、MasterKey、ScopedToken 一律使用驗證器解析出的實際值。
var brokerSecrets = Broker.Configuration.BrokerSecretsValidator.Validate(
    builder.Configuration, builder.Environment, startupLogger);

// ── 資料庫 ──
var dbPath = builder.Configuration.GetValue<string>("Database:Path") ?? "broker.db";
var connectionString = $"Data Source={dbPath}";
builder.Services.AddSingleton(sp => BrokerDb.UseSqlite(connectionString));

// ── 初始化資料庫（17 張表 + 種子資料） ──
// 種子任務的註冊密鑰（RegistrationSecret）：DevelopmentSeed 在非 Development／Testing 環境缺少時拒絕啟動，
// 其他情況只記警告且不建立憑證（見 RegistrationSeedValidator）；密鑰本身不寫進 log。
using (var initDb = BrokerDb.UseSqlite(connectionString))
{
    var initializer = new BrokerDbInitializer(initDb);
    var developmentSeed = builder.Configuration.GetSection(Broker.Configuration.RegistrationSeedValidator.DevelopmentSeedSection).Get<DevelopmentSeedOptions>();
    Broker.Configuration.RegistrationSeedValidator.Validate(
        developmentSeed,
        Broker.Configuration.RegistrationSeedValidator.DevelopmentSeedSection,
        builder.Environment.EnvironmentName,
        startupLogger);
    initializer.Initialize(developmentSeed, RegistrationCredentialSources.DevelopmentSeed);
    WarnIfSeedCredentialHeldRevoked(initializer, Broker.Configuration.RegistrationSeedValidator.DevelopmentSeedSection, startupLogger);
    // Dashboard 種子（管理介面專用 Principal）：只在 Development 環境種入
    var dashboardSeed = builder.Configuration.GetSection(Broker.Configuration.RegistrationSeedValidator.DashboardSeedSection).Get<DevelopmentSeedOptions>();
    if (dashboardSeed?.Enabled == true)
    {
        if (builder.Environment.IsDevelopment())
        {
            Broker.Configuration.RegistrationSeedValidator.Validate(
                dashboardSeed,
                Broker.Configuration.RegistrationSeedValidator.DashboardSeedSection,
                builder.Environment.EnvironmentName,
                startupLogger);
            initializer.Initialize(dashboardSeed, RegistrationCredentialSources.DashboardSeed);
            WarnIfSeedCredentialHeldRevoked(initializer, Broker.Configuration.RegistrationSeedValidator.DashboardSeedSection, startupLogger);
        }
        else
            startupLogger.LogWarning(
                "DashboardSeed is enabled but ignored outside the Development environment (current: {Environment}).",
                builder.Environment.EnvironmentName);
    }
}

// ── Step 2: 加密基礎建設 ──
// ECDH P-256 金鑰對（Singleton，所有 instance 共享同一金鑰）
// 佔位值判斷與格式檢查已由 BrokerSecretsValidator 完成；佔位值時這裡拿到的是本次程序隨機產生的私鑰。
var ecdhPrivateKey = brokerSecrets.EcdhPrivateKeyBase64;
if (!brokerSecrets.EcdhKeyIsEphemeral)
{
    builder.Services.AddSingleton<IEnvelopeCrypto>(sp =>
        new EnvelopeCrypto(ecdhPrivateKey));
}
else
{
    // 開發模式（或明確 opt-in 的 Broker:AllowEphemeralKeys）：金鑰對每次重啟不同
    var crypto = new EnvelopeCrypto(ecdhPrivateKey);
    builder.Services.AddSingleton<IEnvelopeCrypto>(crypto);
    // M-3 修復：不再將完整金鑰記錄到 log（即使是 public key 也不應洩漏至 log aggregation）
    startupLogger.LogWarning(
        "Using auto-generated ECDH key pair. Set Broker:Encryption:EcdhPrivateKeyBase64 in production.");
    var pubKey = crypto.GetBrokerPublicKey();
    startupLogger.LogInformation(
        "Broker public key generated (length={KeyLength}, prefix={KeyPrefix}...)",
        pubKey.Length, pubKey[..Math.Min(8, pubKey.Length)]);
}

// ── Phase 2: 分散式快取（條件式接入） ──
var cacheEnabled = builder.Configuration.GetValue<bool>("CacheCluster:Enabled", false);
CacheClient.IDistributedCache? distributedCache = null;

if (cacheEnabled)
{
    var cacheNodes = builder.Configuration.GetSection("CacheCluster:Nodes").Get<List<string>>()
        ?? new List<string> { "localhost:6380" };
    var cacheOptions = new CacheClient.CacheClientOptions
    {
        Nodes = cacheNodes,
        PoolSize = builder.Configuration.GetValue("CacheCluster:PoolSize", 4),
        ConnectTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("CacheCluster:ConnectTimeoutSeconds", 5)),
        OperationTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("CacheCluster:OperationTimeoutSeconds", 3))
    };

    var rawCache = new CacheClient.DistributedCacheClient(cacheOptions);
    var fallbackCache = new CacheClient.FallbackDecorator(rawCache);
    var cacheFallbackLogger = startupLoggerFactory.CreateLogger("CacheFallback");
    fallbackCache.OnFallback += (operation, ex) =>
    {
        cacheFallbackLogger.LogWarning(ex, "Cache fallback triggered: {Operation}", operation);
    };

    distributedCache = fallbackCache;
    builder.Services.AddSingleton<CacheClient.IDistributedCache>(fallbackCache);
    startupLogger.LogInformation("Cache cluster enabled: nodes={Nodes}", string.Join(", ", cacheNodes));
}

// Session 金鑰存儲（DB 後端，主金鑰加密）
// 實際主金鑰由 BrokerSecretsValidator 提供（佔位值時為本次程序的隨機金鑰）。
var masterKeyBase64 = brokerSecrets.MasterKeyBase64;
var dbSessionKeyStore = new Func<IServiceProvider, DbSessionKeyStore>(sp =>
    new DbSessionKeyStore(sp.GetRequiredService<BrokerDb>(), masterKeyBase64));

if (cacheEnabled && distributedCache != null)
{
    builder.Services.AddSingleton<ISessionKeyStore>(sp =>
        new CacheSessionKeyStore(distributedCache, dbSessionKeyStore(sp)));
}
else
{
    builder.Services.AddSingleton<ISessionKeyStore>(sp => dbSessionKeyStore(sp));
}

// ── Step 3: Token + Session + Epoch ──
// 實際簽章密鑰由 BrokerSecretsValidator 提供（佔位值時為本次程序的隨機密鑰）。
var tokenSecret = brokerSecrets.ScopedTokenSecret;
var tokenIssuer = builder.Configuration.GetValue<string>("Broker:ScopedToken:Issuer") ?? "broker-control-plane";
var tokenAudience = builder.Configuration.GetValue<string>("Broker:ScopedToken:Audience") ?? "broker-agents";
var tokenExpMin = builder.Configuration.GetValue<int>("Broker:ScopedToken:ExpirationMinutes");
if (tokenExpMin <= 0) tokenExpMin = 15;

builder.Services.AddSingleton<IScopedTokenService>(sp =>
    new ScopedTokenService(tokenSecret, tokenIssuer, tokenAudience, tokenExpMin));

// Session 有效時間與最長存活時間（Broker:Session）；不合理的設定（含 TTL 不小於金鑰快取存活時間）在啟動時即失敗。
var sessionLifetime = builder.Configuration.GetSection(SessionLifetimeOptions.SectionName).Get<SessionLifetimeOptions>()
    ?? new SessionLifetimeOptions();
sessionLifetime.Validate();
builder.Services.AddSingleton(sessionLifetime);
builder.Services.AddSingleton<ISessionService>(sp =>
    new SessionService(sp.GetRequiredService<BrokerDb>(), sessionLifetime));

// Session 註冊憑證（只存雜湊）與 broker 簽發憑證的存活時間（Broker:RegistrationCredential）
var registrationCredentialOptions = builder.Configuration.GetSection(RegistrationCredentialOptions.SectionName).Get<RegistrationCredentialOptions>()
    ?? new RegistrationCredentialOptions();
registrationCredentialOptions.Validate();
builder.Services.AddSingleton(registrationCredentialOptions);
builder.Services.AddSingleton<IRegistrationCredentialService>(sp =>
    new RegistrationCredentialService(sp.GetRequiredService<BrokerDb>()));

if (cacheEnabled && distributedCache != null)
{
    builder.Services.AddSingleton<IRevocationService>(sp =>
        new CacheRevocationService(sp.GetRequiredService<BrokerDb>(), distributedCache));
}
else
{
    builder.Services.AddSingleton<IRevocationService>(sp =>
        new RevocationService(sp.GetRequiredService<BrokerDb>()));
}

// ── Step 4: Audit Trail ──
builder.Services.AddSingleton<IAuditService>(sp =>
    new AuditService(sp.GetRequiredService<BrokerDb>()));

// ── Step 5: Capability Catalog + Policy Engine ──
builder.Services.AddSingleton<ISchemaValidator, SchemaValidator>();
builder.Services.AddSingleton<ITaskRouter, TaskRouter>();

if (cacheEnabled && distributedCache != null)
{
    builder.Services.AddSingleton<ICapabilityCatalog>(sp =>
        new CacheCapabilityCatalog(
            sp.GetRequiredService<BrokerDb>(),
            distributedCache,
            new CapabilityCatalog(sp.GetRequiredService<BrokerDb>())));
}
else
{
    builder.Services.AddSingleton<ICapabilityCatalog>(sp =>
        new CapabilityCatalog(sp.GetRequiredService<BrokerDb>()));
}

// M-2 修復：PolicyEngine 黑名單從配置讀取（無配置時使用預設值）
var policyOptions = builder.Configuration.GetSection("PolicyEngine").Get<PolicyEngineOptions>()
    ?? new PolicyEngineOptions();
builder.Services.AddSingleton<IPolicyEngine>(sp =>
    new PolicyEngine(sp.GetRequiredService<ISchemaValidator>(), policyOptions));
var llmProxyOptions = builder.Configuration.GetSection("LlmProxy").Get<LlmProxyOptions>()
    ?? new LlmProxyOptions();
builder.Services.AddSingleton(llmProxyOptions);
// [MONITORING EXTRACTION] LLM 觀測：raw LlmProxyService 用 typed HttpClient，外面包 Metered 裝飾器
builder.Services.AddSingleton<BrokerCore.Services.LlmProxyMetrics>();
builder.Services.AddHttpClient<LlmProxyService>();
builder.Services.AddSingleton<ILlmProxyService>(sp =>
    new BrokerCore.Services.MeteredLlmProxyService(
        sp.GetRequiredService<LlmProxyService>(),
        sp.GetRequiredService<BrokerCore.Services.LlmProxyMetrics>()));
// [MONITORING EXTRACTION] Health Score 子系統（leader-guard 單機永遠 PRIMARY）。
// 它依賴 IWorkerRegistry（FunctionPool），且本質是監控 worker pool 健康，
// 因此只在 FunctionPool 啟用時註冊；否則 broker 啟動會無法 resolve IWorkerRegistry。
if (builder.Configuration.GetValue<bool>("FunctionPool:Enabled", false))
{
    builder.Services.AddSingleton<Broker.Services.ILeaderElection, Broker.Services.SingleNodeLeaderElection>();
    builder.Services.AddSingleton<Broker.Services.LeaderGuard>();
    builder.Services.AddSingleton<Broker.Services.HealthScoreService>();
    builder.Services.AddSingleton<Broker.Services.HealthScoreSnapshotService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.HealthScoreSnapshotService>());
}
var highLevelLlmOptions = builder.Configuration.GetSection("HighLevelLlm").Get<Broker.Services.HighLevelLlmOptions>()
    ?? new Broker.Services.HighLevelLlmOptions();
builder.Services.AddSingleton(highLevelLlmOptions);
var highLevelExecutionModelPolicy = builder.Configuration.GetSection("HighLevelExecutionModelPolicy").Get<Broker.Services.HighLevelExecutionModelPolicyOptions>()
    ?? new Broker.Services.HighLevelExecutionModelPolicyOptions();
builder.Services.AddSingleton(highLevelExecutionModelPolicy);
var deploymentSecretOptions = builder.Configuration.GetSection("DeploymentSecrets").Get<Broker.Services.AzureIisDeploymentSecretResolverOptions>()
    ?? new Broker.Services.AzureIisDeploymentSecretResolverOptions();
builder.Services.AddSingleton(deploymentSecretOptions);
var googleDriveDeliveryOptions = builder.Configuration.GetSection("GoogleDriveDelivery").Get<Broker.Services.GoogleDriveDeliveryOptions>()
    ?? new Broker.Services.GoogleDriveDeliveryOptions();
builder.Services.AddSingleton(googleDriveDeliveryOptions);
var artifactDownloadOptions = builder.Configuration.GetSection("ArtifactDownload").Get<Broker.Services.BrokerArtifactDownloadOptions>()
    ?? new Broker.Services.BrokerArtifactDownloadOptions();
builder.Services.AddSingleton(artifactDownloadOptions);
var portalAuthOptions = builder.Configuration.GetSection("PortalAuth").Get<Broker.Services.PortalAuthOptions>()
    ?? new Broker.Services.PortalAuthOptions();
builder.Services.AddSingleton(portalAuthOptions);
var tdxOptions = builder.Configuration.GetSection("Tdx").Get<Broker.Services.TdxOptions>()
    ?? new Broker.Services.TdxOptions();
builder.Services.AddSingleton(tdxOptions);
builder.Services.AddSingleton(sp =>
    new Broker.Services.TdxApiService(
        sp.GetRequiredService<Broker.Services.TdxOptions>(),
        new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
        sp.GetRequiredService<ILogger<Broker.Services.TdxApiService>>()));
var toolSpecRegistryOptions = builder.Configuration.GetSection("ToolSpecRegistry").Get<Broker.Services.ToolSpecRegistryOptions>()
    ?? new Broker.Services.ToolSpecRegistryOptions();
builder.Services.AddSingleton(toolSpecRegistryOptions);
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IConfiguration>().GetSection("WorkerAuth").Get<WorkerIdentityAuthOptions>()
    ?? new WorkerIdentityAuthOptions());
builder.Services.AddSingleton<WorkerAuthNonceStore>();
builder.Services.AddSingleton<WorkerIdentityAuthService>();
builder.Services.AddSingleton<Broker.Services.IToolSpecRegistry, Broker.Services.ToolSpecRegistry>();
builder.Services.AddSingleton<Broker.Services.LocalAdminAuthService>();
builder.Services.AddSingleton<Broker.Services.PortalLineVerificationService>();
builder.Services.AddSingleton<Broker.Services.PortalAuthService>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewStateService>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewRestatementService>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewStateMachine>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewTemplateCatalogService>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewProjectDefinitionCompiler>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewWorkflowDesignService>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewPdfRenderService>();
builder.Services.AddSingleton<Broker.Services.HighLevelLineWorkspaceService>();
builder.Services.AddSingleton<Broker.Services.HighLevelInteractionRecorder>();
builder.Services.AddSingleton<Broker.Services.SidecarPublicUrlResolver>();
builder.Services.AddSingleton<Broker.Services.BrokerArtifactDownloadService>();
builder.Services.AddSingleton<Broker.Services.ApprovalLinkService>();
builder.Services.AddSingleton<Broker.Services.LineArtifactDeliveryService>();
builder.Services.AddSingleton<Broker.Services.HighLevelSystemScaffoldSpecStore>();
builder.Services.AddSingleton<Broker.Services.HighLevelSystemScaffoldIterationStore>();
builder.Services.AddSingleton<Broker.Services.HighLevelSystemScaffoldProgressStore>();
builder.Services.AddSingleton<Broker.Services.HighLevelDocumentArtifactService>();
builder.Services.AddSingleton<Broker.Services.IWebContentProvider, Broker.Services.WebSearchHelperContentProvider>();
builder.Services.AddSingleton<Broker.Services.IWebReportLlm, Broker.Services.LlmProxyReportLlm>();
builder.Services.AddSingleton<Broker.Services.WebReportSynthesisService>();
builder.Services.AddSingleton<Broker.Services.HighLevelCodeArtifactService>();
builder.Services.AddSingleton<Broker.Services.HighLevelSystemScaffoldService>();
builder.Services.AddSingleton<Broker.Services.HighLevelSiteRebuildService>();
builder.Services.AddSingleton<Broker.Services.IBrowserExecutionRequestBuilder, Broker.Services.BrowserExecutionRequestBuilder>();
builder.Services.AddSingleton<Broker.Services.AzureIisDeploymentTargetService>();
builder.Services.AddSingleton<Broker.Services.IAzureIisDeploymentRequestBuilder, Broker.Services.AzureIisDeploymentRequestBuilder>();
builder.Services.AddHttpClient<Broker.Services.AzureIisDeploymentHealthCheckService>();
builder.Services.AddSingleton<Broker.Services.AzureIisDeploymentPreviewService>();
builder.Services.AddSingleton<Broker.Services.IAzureIisDeploymentSecretResolver, Broker.Services.AzureIisDeploymentSecretResolver>();
builder.Services.AddSingleton<Broker.Services.IProcessRunner, Broker.Services.ProcessRunner>();
builder.Services.AddSingleton<Broker.Services.AzureIisDeploymentExecutionService>();
builder.Services.AddSingleton<Broker.Services.HighLevelWorkflowAdminService>();
builder.Services.AddHttpClient<Broker.Services.GoogleDriveOAuthService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Bricks4Agent-GoogleDriveOAuth/1.0");
});
builder.Services.AddHttpClient<Broker.Services.GoogleDriveShareService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(60);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Bricks4Agent-GoogleDriveDelivery/1.0");
});
builder.Services.AddHttpClient<Broker.Services.BrowserExecutionPreviewService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Bricks4Agent-BrowserPreview/1.0");
});
builder.Services.AddHttpClient<Broker.Services.BrowserExecutionRuntimeService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(45);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Bricks4Agent-BrowserRuntime/1.0");
});
builder.Services.AddHostedService<Broker.Services.ToolSpecCapabilitySyncService>();

// ── Step 6 + 7: BrokerService + ExecutionDispatcher ──
// Phase 3: 功能池（條件式啟用）
var poolEnabled = builder.Configuration.GetValue<bool>("FunctionPool:Enabled", false);
var strictMode = builder.Configuration.GetValue<bool>("FunctionPool:StrictMode", false);

if (poolEnabled)
{
    var poolConfig = new PoolConfig
    {
        ListenPort = builder.Configuration.GetValue("FunctionPool:ListenPort", 7000),
        BindAddress = builder.Configuration.GetValue("FunctionPool:BindAddress", "0.0.0.0") ?? "0.0.0.0",
        DispatchTimeout = TimeSpan.FromSeconds(
            builder.Configuration.GetValue("FunctionPool:DispatchTimeoutSeconds", 30)),
        MaxRetries = builder.Configuration.GetValue("FunctionPool:MaxRetries", 2),
        HeartbeatTimeout = TimeSpan.FromSeconds(
            builder.Configuration.GetValue("FunctionPool:HeartbeatTimeoutSeconds", 30)),
        HealthCheckInterval = TimeSpan.FromSeconds(
            builder.Configuration.GetValue("FunctionPool:HealthCheckIntervalSeconds", 10)),
        MaxWorkers = builder.Configuration.GetValue("FunctionPool:MaxWorkers", 100)
    };
    // 個別能力的分派超時（例如生成要等 worker 的建置逾時）：FunctionPool:CapabilityDispatchTimeoutSeconds:{能力 id}
    poolConfig.AddCapabilityDispatchTimeouts(builder.Configuration
        .GetSection("FunctionPool:CapabilityDispatchTimeoutSeconds")
        .GetChildren()
        .Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value)));

    builder.Services.AddSingleton(poolConfig);
    builder.Services.AddSingleton<IWorkerRegistry, WorkerRegistry>();
    builder.Services.AddSingleton<PoolListener>();
    builder.Services.AddSingleton(sp =>
        new WorkerHealthMonitor(
            sp.GetRequiredService<IWorkerRegistry>(),
            sp.GetRequiredService<PoolConfig>(),
            sp.GetRequiredService<ILogger<WorkerHealthMonitor>>(),
            sp.GetService<IObservationService>()));

    if (strictMode)
    {
        // 生產模式：StrictPoolDispatcher — 無 Worker 就 fail，絕不降級
        builder.Services.AddSingleton<IExecutionDispatcher>(sp =>
        {
            var poolDispatcher = new PoolDispatcher(
                sp.GetRequiredService<IWorkerRegistry>(),
                poolConfig,
                sp.GetRequiredService<ILogger<PoolDispatcher>>());
            return new StrictPoolDispatcher(
                poolDispatcher,
                sp.GetRequiredService<ILogger<StrictPoolDispatcher>>());
        });
    }
    else
    {
        // 開發/測試模式：FallbackDispatcher — Pool + InProcess 降級
        builder.Services.AddSingleton<IExecutionDispatcher>(sp =>
        {
            var inProcess = new InProcessDispatcher(
                sp.GetRequiredService<ILogger<InProcessDispatcher>>(),
                null,
                sp.GetRequiredService<Broker.Services.IProcessRunner>(),
                sp.GetRequiredService<AgentSpawnService>(),
                sp.GetRequiredService<BrokerDb>(),
                sp.GetRequiredService<BrokerCore.Services.EmbeddingService>(),
                sp.GetRequiredService<BrokerCore.Services.RagPipelineService>(),
                sp.GetRequiredService<Broker.Services.BrowserExecutionRuntimeService>(),
                sp.GetRequiredService<Broker.Services.AzureIisDeploymentExecutionService>(),
                sp.GetRequiredService<Broker.Services.GoogleDriveShareService>(),
                sp.GetRequiredService<Broker.Services.TdxApiService>());
            var poolDispatcher = new PoolDispatcher(
                sp.GetRequiredService<IWorkerRegistry>(),
                poolConfig,
                sp.GetRequiredService<ILogger<PoolDispatcher>>());
            return new FallbackDispatcher(
                poolDispatcher, inProcess, inProcess.CanHandle,
                sp.GetRequiredService<ILogger<FallbackDispatcher>>());
        });
    }

    // ── Container Manager（條件式啟用）──
    var containerEnabled = builder.Configuration.GetValue<bool>("FunctionPool:ContainerManager:Enabled", false);
    if (containerEnabled)
    {
        var containerConfig = new ContainerConfig
        {
            Runtime = builder.Configuration.GetValue("FunctionPool:ContainerManager:Runtime", "docker") ?? "docker",
            NetworkName = builder.Configuration.GetValue("FunctionPool:ContainerManager:NetworkName", "bricks4agent_worker-net") ?? "bricks4agent_worker-net",
            MaxContainersPerType = builder.Configuration.GetValue("FunctionPool:ContainerManager:MaxContainersPerType", 3),
            SpawnTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("FunctionPool:ContainerManager:SpawnTimeoutSeconds", 120)),
            AutoRespawn = builder.Configuration.GetValue("FunctionPool:ContainerManager:AutoRespawn", true),
            BrokerHostForWorkers = builder.Configuration.GetValue("FunctionPool:ContainerManager:BrokerHostForWorkers", "broker") ?? "broker",
            BrokerPortForWorkers = builder.Configuration.GetValue("FunctionPool:ContainerManager:BrokerPortForWorkers", 7000),
            AgentBrokerUrl = builder.Configuration.GetValue("FunctionPool:ContainerManager:AgentBrokerUrl", "http://broker:5000") ?? "http://broker:5000",
            // §13.2 hardening knobs (the hardening flags themselves are always applied)
            AllowAgentDefaultNetwork = builder.Configuration.GetValue("FunctionPool:ContainerManager:AllowAgentDefaultNetwork", false),
            DefaultMemoryLimit = builder.Configuration.GetValue("FunctionPool:ContainerManager:DefaultMemoryLimit", "512m") ?? "512m",
            DefaultPidsLimit = builder.Configuration.GetValue("FunctionPool:ContainerManager:DefaultPidsLimit", 256)
        };
        foreach (var allowedRoot in builder.Configuration.GetSection("FunctionPool:ContainerManager:AllowedHostPathRoots").GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(allowedRoot.Value))
                containerConfig.AllowedHostPathRoots.Add(allowedRoot.Value);
        }

        // Load worker image configs from configuration
        var imageSection = builder.Configuration.GetSection("FunctionPool:ContainerManager:WorkerImages");
        foreach (var child in imageSection.GetChildren())
        {
            containerConfig.WorkerImages[child.Key] = new WorkerImageConfig
            {
                Image = child.GetValue<string>("Image") ?? $"bricks4agent/{child.Key}:latest",
                MemoryLimit = child.GetValue<string>("MemoryLimit"),
                CpuLimit = child.GetValue<string>("CpuLimit"),
                User = child.GetValue<string>("User"),
                NetworkName = child.GetValue<string>("NetworkName"),
                PidsLimit = child.GetValue<int?>("PidsLimit")
            };

            // Load environment
            var envSection = child.GetSection("Environment");
            foreach (var env in envSection.GetChildren())
                containerConfig.WorkerImages[child.Key].Environment[env.Key] = env.Value ?? "";

            // Load volumes
            var volSection = child.GetSection("Volumes");
            foreach (var vol in volSection.GetChildren())
                containerConfig.WorkerImages[child.Key].Volumes.Add(vol.Value ?? "");

            // Load ports
            var portSection = child.GetSection("Ports");
            foreach (var port in portSection.GetChildren())
                containerConfig.WorkerImages[child.Key].Ports.Add(port.Value ?? "");
        }

        builder.Services.AddSingleton(containerConfig);
        builder.Services.AddSingleton<IContainerManager>(sp =>
            new ContainerManager(containerConfig, sp.GetRequiredService<ILogger<ContainerManager>>()));

        startupLogger.LogInformation(
            "Container manager enabled: runtime={Runtime}, workerTypes=[{Types}]",
            containerConfig.Runtime, string.Join(", ", containerConfig.WorkerImages.Keys));
    }
    else
    {
        // Register a no-op container manager so endpoints can still resolve
        builder.Services.AddSingleton<IContainerManager>(sp =>
            new NoOpContainerManager());
    }

    startupLogger.LogInformation(
        "Function pool enabled: port={Port}, strictMode={Strict}",
        poolConfig.ListenPort, strictMode);
}
else
{
    builder.Services.AddSingleton<IExecutionDispatcher>(sp =>
        new InProcessDispatcher(
            sp.GetRequiredService<ILogger<InProcessDispatcher>>(),
            null,
            sp.GetRequiredService<Broker.Services.IProcessRunner>(),
            sp.GetRequiredService<AgentSpawnService>(),
            sp.GetRequiredService<BrokerDb>(),
            sp.GetRequiredService<BrokerCore.Services.EmbeddingService>(),
            sp.GetRequiredService<BrokerCore.Services.RagPipelineService>(),
            sp.GetRequiredService<Broker.Services.BrowserExecutionRuntimeService>(),
            sp.GetRequiredService<Broker.Services.AzureIisDeploymentExecutionService>(),
            sp.GetRequiredService<Broker.Services.GoogleDriveShareService>(),
            sp.GetRequiredService<Broker.Services.TdxApiService>()));
    builder.Services.AddSingleton<IContainerManager>(sp => new NoOpContainerManager());
}

// ── Phase 4: 因果工作流服務 ──
builder.Services.AddSingleton<ISharedContextService>(sp =>
    new SharedContextService(
        sp.GetRequiredService<BrokerDb>(),
        sp.GetRequiredService<IAuditService>()));

builder.Services.AddSingleton<IPlanService>(sp =>
    new PlanService(
        sp.GetRequiredService<BrokerDb>(),
        sp.GetRequiredService<IAuditService>()));

builder.Services.AddSingleton<IObservationService>(sp =>
    new ObservationService(
        sp.GetRequiredService<BrokerDb>(),
        sp.GetRequiredService<IAuditService>()));

// ── Agent Spawn Service ──
builder.Services.AddSingleton<AgentSpawnService>(sp =>
    new AgentSpawnService(
        sp.GetRequiredService<BrokerDb>(),
        sp.GetRequiredService<IRegistrationCredentialService>(),
        sp.GetRequiredService<ISessionService>()));

// ── Embedding Service（向量嵌入） ──
var embeddingConfig = builder.Configuration.GetSection("Embedding").Get<BrokerCore.Services.EmbeddingOptions>()
    ?? new BrokerCore.Services.EmbeddingOptions();
builder.Services.AddSingleton(embeddingConfig);
builder.Services.AddSingleton<BrokerCore.Services.EmbeddingService>(sp =>
    new BrokerCore.Services.EmbeddingService(
        sp.GetRequiredService<BrokerCore.Services.EmbeddingOptions>(),
        msg => sp.GetRequiredService<ILogger<BrokerCore.Services.EmbeddingService>>().LogInformation(msg)));
startupLogger.LogInformation("Embedding service: enabled={Enabled}, model={Model}",
    embeddingConfig.Enabled, embeddingConfig.Model);

// ── RAG Pipeline Service（查詢改寫 + 重排序 + 嵌入快取） ──
var ragPipelineConfig = builder.Configuration.GetSection("RagPipeline").Get<BrokerCore.Services.RagPipelineOptions>()
    ?? new BrokerCore.Services.RagPipelineOptions();
// 同步 Ollama base URL
if (string.IsNullOrEmpty(ragPipelineConfig.OllamaBaseUrl) || ragPipelineConfig.OllamaBaseUrl == "http://localhost:11434")
    ragPipelineConfig.OllamaBaseUrl = embeddingConfig.BaseUrl;
builder.Services.AddSingleton(ragPipelineConfig);
builder.Services.AddSingleton<BrokerCore.Services.RagPipelineService>(sp =>
    new BrokerCore.Services.RagPipelineService(
        sp.GetRequiredService<BrokerCore.Services.RagPipelineOptions>(),
        msg => sp.GetRequiredService<ILogger<BrokerCore.Services.RagPipelineService>>().LogInformation(msg)));
startupLogger.LogInformation(
    "RAG Pipeline: queryRewrite={QR}, rerank={RR}, cache={C}",
    ragPipelineConfig.QueryRewriteEnabled, ragPipelineConfig.RerankEnabled, ragPipelineConfig.CacheEnabled);

// ── LINE Chat Gateway（LINE ↔ LLM 閘道） ──
var lineChatConfig = builder.Configuration.GetSection("LineChatGateway").Get<Broker.Services.LineChatGatewayOptions>()
    ?? new Broker.Services.LineChatGatewayOptions();
builder.Services.AddSingleton(lineChatConfig);
builder.Services.AddHttpClient("high-level-llm", client =>
{
    client.BaseAddress = new Uri((highLevelLlmOptions.BaseUrl ?? "http://localhost:11434").TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(Math.Max(30, highLevelLlmOptions.TimeoutSeconds));
    if (AnthropicMessagesAdapter.IsAnthropicProvider(highLevelLlmOptions.Provider))
    {
        AnthropicMessagesAdapter.ConfigureHeaders(client, highLevelLlmOptions.ApiKey);
    }
    else if (!string.IsNullOrWhiteSpace(highLevelLlmOptions.ApiKey))
    {
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", highLevelLlmOptions.ApiKey);
    }
});
builder.Services.AddSingleton<Broker.Services.LineChatGateway>();
builder.Services.AddSingleton<Broker.Services.HighLevelQueryToolMediator>();
builder.Services.AddSingleton<Broker.Services.HighLevelRelationQueryService>();
builder.Services.AddSingleton<Broker.Services.IHighLevelExecutionModelPlanner, Broker.Services.HighLevelExecutionModelPlanner>();
startupLogger.LogInformation(
    "LINE Chat Gateway: enabled={Enabled}, rag={Rag}, provider={Provider}, model={Model}",
    lineChatConfig.Enabled, lineChatConfig.RagEnabled, highLevelLlmOptions.Provider, highLevelLlmOptions.DefaultModel);
var highLevelCoordinatorConfig = builder.Configuration.GetSection("HighLevelCoordinator").Get<Broker.Services.HighLevelCoordinatorOptions>()
    ?? new Broker.Services.HighLevelCoordinatorOptions();
builder.Services.AddSingleton(highLevelCoordinatorConfig);

// ── 受治理生成（HighLevelCoordinator:Generation:SystemScaffoldMode = Governed）──
// broker 依任務啟動受控代理；代理經三個生成能力走 PEP，generation-worker 產出原型，
// broker 驗證產物（路徑、sha256）後交付。Legacy（預設）時這些服務不會被用到，
// 但交付與 watchdog 仍會接手先前以 Governed 模式啟動、尚未結束的任務。
var governedGenerationOptions = builder.Configuration.GetSection(Broker.Services.GovernedGenerationOptions.SectionName).Get<Broker.Services.GovernedGenerationOptions>()
    ?? new Broker.Services.GovernedGenerationOptions();
builder.Services.AddSingleton(governedGenerationOptions);
builder.Services.AddSingleton<Broker.Services.AgentContainerLauncher>();
builder.Services.AddSingleton<Broker.Services.GovernedGenerationRunStore>();
builder.Services.AddSingleton<Broker.Services.IGovernedGenerationReadiness, Broker.Services.GovernedGenerationReadiness>();
builder.Services.AddSingleton<Broker.Services.GovernedGenerationLauncher>();
builder.Services.AddSingleton<Broker.Services.GenerationDeliverySignal>();
builder.Services.AddSingleton<Broker.Services.GenerationPackageIngestor>();
builder.Services.AddSingleton<Broker.Services.IGeneratedPackageDelivery, Broker.Services.LineGeneratedPackageDelivery>();
builder.Services.AddSingleton<Broker.Services.GenerationDeliveryService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.GenerationDeliveryService>());

builder.Services.AddSingleton<Broker.Services.HighLevelCoordinator>();
builder.Services.AddSingleton<Broker.Services.BrowserBindingService>();
startupLogger.LogInformation(
    "High-level coordinator: draftTtlMinutes={DraftTtlMinutes}, maxDraftSummaryLength={MaxDraftSummaryLength}",
    highLevelCoordinatorConfig.DraftTtlMinutes,
    highLevelCoordinatorConfig.MaxDraftSummaryLength);
startupLogger.LogInformation(
    "Tool spec registry: root={Root}",
    toolSpecRegistryOptions.Root);

builder.Services.AddSingleton<IToolSpecStatusChecker>(sp =>
    new Broker.Services.ToolSpecStatusChecker(
        sp.GetRequiredService<IToolSpecRegistry>(),
        sp.GetRequiredService<ILogger<Broker.Services.ToolSpecStatusChecker>>()));

builder.Services.AddSingleton<IApprovalNotifier, Broker.Services.LineApprovalNotifier>();

builder.Services.AddSingleton<IBrokerService>(sp =>
    new BrokerService(
        sp.GetRequiredService<BrokerDb>(),
        sp.GetRequiredService<IPolicyEngine>(),
        sp.GetRequiredService<IAuditService>(),
        sp.GetRequiredService<ICapabilityCatalog>(),
        sp.GetRequiredService<ISessionService>(),
        sp.GetRequiredService<IRevocationService>(),
        sp.GetRequiredService<ITaskRouter>(),
        // generate_scaffold 的結果先經 broker 驗證並收下產物（路徑、sha256、證據）才算成功；
        // 包在註冊的分派器外層，不論分派器是 pool、strict 或 in-process（測試替換的也一樣）。
        new Broker.Services.GenerationIngestingDispatcher(
            sp.GetRequiredService<IExecutionDispatcher>(),
            sp.GetRequiredService<Broker.Services.GenerationPackageIngestor>()),
        sp.GetRequiredService<IToolSpecStatusChecker>(),
        sp.GetRequiredService<IApprovalNotifier>()));

builder.Services.AddSingleton<IPlanEngine>(sp =>
    new PlanEngine(
        sp.GetRequiredService<IPlanService>(),
        sp.GetRequiredService<IBrokerService>(),
        sp.GetRequiredService<ISharedContextService>(),
        sp.GetRequiredService<IAuditService>(),
        sp.GetRequiredService<IObservationService>()));

// [新增 2026-10 AnthonyLee] 異常偵測報告：規則判斷異常、LLM 只寫敘述（Services/Observability）。
// 排程預設關閉（Observability:IncidentReport:Enabled），只註冊服務不改變既有行為。
builder.Services.AddIncidentReporting(builder.Configuration);

var app = builder.Build();

app.Logger.LogInformation("Broker database path: {DbPath}", dbPath);

// [新增 2026-10 AnthonyLee] API 回應狀態碼計數，必須在最外層：
// 稽核中介層排在加密與驗證之後，被前面擋下的 4xx 不會進 audit_events，這裡補上計數（只計數、不改回應）。
app.UseHttpStatusMetrics();

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/dev/admin", StringComparison.OrdinalIgnoreCase) ||
        context.Request.Path.Equals("/dev/line-users", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    await next();
});

app.UseDevEndpointGuard();
app.UseBrokerExceptionHandling();

// ── 靜態檔案（Dashboard UI）── 必須在加密/認證中間件之前
app.UseStaticFiles();

var browserAssetsRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "..", "..", "javascript", "browser"));
var userPortalRoot = Path.Combine(browserAssetsRoot, "user-portal");
var uiComponentsRoot = Path.Combine(browserAssetsRoot, "ui_components");
if (Directory.Exists(userPortalRoot))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(userPortalRoot),
        RequestPath = "/portal"
    });
}
if (Directory.Exists(uiComponentsRoot))
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(uiComponentsRoot),
        RequestPath = "/ui_components"
    });
}

// ── Middleware 管線（順序關鍵） ──
// [0] BodySizeLimitMiddleware（H-10 修復：防止 DoS 超大 payload）
var maxBodyBytes = builder.Configuration.GetValue<long>("Broker:MaxRequestBodyBytes", 1_048_576); // 1MB default
app.UseBodySizeLimit(maxBodyBytes);
var brokerIpRateLimitOptions = builder.Configuration.GetSection("Broker:IpRateLimit").Get<BrokerIpRateLimitOptions>()
    ?? new BrokerIpRateLimitOptions();
app.UseBrokerIpRateLimit(brokerIpRateLimitOptions);
// [3] EncryptionMiddleware（信封解密/加密）
app.UseEnvelopeEncryption();
// [4] WorkerIdentityAuthMiddleware（worker caller 驗證）
app.UseWorkerIdentityAuth();
// [5] BrokerAuthMiddleware（Token 驗證）
app.UseBrokerAuth();
// [6] AuditMiddleware（稽核記錄）
app.UseBrokerAudit();

// ── Dashboard（靜態 HTML，由 UseStaticFiles 提供） ──
// Dashboard JS 內建完整 ECDH+AES-GCM 加密客戶端，所有 API 呼叫走加密 POST

// ── API 路由 ──
// 驗證政策以端點 metadata 標示（Broker.Helpers.BrokerAuthPolicy）：/api/v1 預設 ScopedToken，
// 只有明確標示的群組或端點（health、sessions/register、簽章連結、local-admin、portal、worker 簽章路由）例外。
var api = app.MapGroup("/api/v1")
    .WithMetadata(new Broker.Helpers.BrokerAuthPolicyMetadata(Broker.Helpers.BrokerAuthPolicy.ScopedToken));

// L-7 修復：健康檢查同時支援 GET（標準 LB 探測）和 POST（向後相容）
var brokerCrypto = app.Services.GetRequiredService<IEnvelopeCrypto>();
var healthHandler = () => Results.Ok(new
{
    status = "ok",
    timestamp = DateTime.UtcNow,
    broker_public_key = brokerCrypto.GetBrokerPublicKey()
});
api.MapGet("/health", healthHandler)
    .WithMetadata(new Broker.Helpers.BrokerAuthPolicyMetadata(Broker.Helpers.BrokerAuthPolicy.Public));
api.MapPost("/health", healthHandler)
    .WithMetadata(new Broker.Helpers.BrokerAuthPolicyMetadata(Broker.Helpers.BrokerAuthPolicy.Public));

// ── Dev RAG test（/dev/ 路徑已被 Encryption+Auth middleware 排除，只允許 localhost 存取） ──
{
    app.MapPost("/dev/rag-test", async (HttpContext ctx,
        BrokerCore.Data.BrokerDb ragTestDb,
        BrokerCore.Services.EmbeddingService ragTestEmbed,
        BrokerCore.Services.RagPipelineService ragTestPipeline) =>
    {
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(ctx.Request.Body);
        var retrieveRequest = BrokerCore.Services.RagRetrieveRequest.FromArgs(body);
        if (string.IsNullOrWhiteSpace(retrieveRequest.Query))
            return Results.BadRequest(new { error = "query is required" });

        var retrieveTaskId = retrieveRequest.TaskId ?? "global";
        var retrieval = new BrokerCore.Services.RagRetrievalService(
            ragTestDb,
            ragTestEmbed,
            ragTestPipeline,
            message => app.Logger.LogWarning("{Message}", message));
        var response = await retrieval.RetrieveAsync(retrieveRequest, retrieveTaskId, ctx.RequestAborted);
        const string consumerLawPrefix = "消費者保護法:";
        var articleCount = ragTestDb.GetAll<BrokerCore.Models.SharedContextEntry>()
            .Count(e => e.Key.StartsWith(consumerLawPrefix, StringComparison.Ordinal));
        var consumerLawVectors = ragTestDb.GetAll<BrokerCore.Models.VectorEntry>()
            .Where(v => v.SourceKey.StartsWith(consumerLawPrefix, StringComparison.Ordinal))
            .ToList();
        var currentModelVectorCount = consumerLawVectors
            .Count(v => string.Equals(v.EmbeddingModel, ragTestEmbed.ModelName, StringComparison.Ordinal));

        return Results.Ok(new
        {
            query = response.Query,
            mode = response.Mode,
            results = response.Results,
            total = response.Total,
            bm25_candidates = response.Bm25Candidates,
            vector_candidates = response.VectorCandidates,
            pipeline = response.Pipeline,
            stats = new
            {
                articles = articleCount,
                embedding_model = ragTestEmbed.ModelName,
                vectors = currentModelVectorCount,
                vectors_current_model = currentModelVectorCount,
                vectors_all_models = consumerLawVectors.Count
            }
        });
    });

    // ── Dev RAG import: JSON ──
    app.MapPost("/dev/rag-import-json", async (HttpContext ctx,
        BrokerCore.Data.BrokerDb importDb,
        BrokerCore.Services.EmbeddingService importEmbed,
        ILoggerFactory loggerFactory) =>
    {
        var logger = loggerFactory.CreateLogger("DevRagImportJson");
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(ctx.Request.Body);

        var tag = body.TryGetProperty("tag", out var t) ? t.GetString() ?? "" : "";
        var taskId = body.TryGetProperty("task_id", out var tid) ? tid.GetString() ?? "global" : "global";

        if (string.IsNullOrWhiteSpace(tag))
            return Results.BadRequest(new { error = "tag is required" });

        string jsonContent;
        if (body.TryGetProperty("data", out var dataEl))
        {
            if (dataEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                jsonContent = dataEl.GetRawText();
            else if (dataEl.ValueKind == System.Text.Json.JsonValueKind.String)
                jsonContent = dataEl.GetString() ?? "[]";
            else
                return Results.BadRequest(new { error = "data must be a JSON array or string" });
        }
        else
            return Results.BadRequest(new { error = "data is required" });

        var result = await Broker.Scripts.RagIngestService.ImportJsonAsync(
            jsonContent, tag, taskId, importDb, importEmbed, logger);

        return Results.Ok(new
        {
            inserted = result.Inserted, skipped = result.Skipped,
            embedded = result.Embedded, errors = result.Errors
        });
    });

    // ── Dev RAG import: CSV ──
    app.MapPost("/dev/rag-import-csv", async (HttpContext ctx,
        BrokerCore.Data.BrokerDb importDb,
        BrokerCore.Services.EmbeddingService importEmbed,
        ILoggerFactory loggerFactory) =>
    {
        var logger = loggerFactory.CreateLogger("DevRagImportCsv");
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(ctx.Request.Body);

        var tag = body.TryGetProperty("tag", out var t) ? t.GetString() ?? "" : "";
        var taskId = body.TryGetProperty("task_id", out var tid) ? tid.GetString() ?? "global" : "global";

        if (string.IsNullOrWhiteSpace(tag))
            return Results.BadRequest(new { error = "tag is required" });

        var csvContent = body.TryGetProperty("data", out var dataEl) ? dataEl.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(csvContent))
            return Results.BadRequest(new { error = "data (CSV string) is required" });

        var result = await Broker.Scripts.RagIngestService.ImportCsvAsync(
            csvContent, tag, taskId, importDb, importEmbed, logger);

        return Results.Ok(new
        {
            inserted = result.Inserted, skipped = result.Skipped,
            embedded = result.Embedded, errors = result.Errors
        });
    });

    // ── Dev RAG import: Web ──
    app.MapPost("/dev/rag-import-web", async (HttpContext ctx,
        BrokerCore.Data.BrokerDb importDb,
        BrokerCore.Services.EmbeddingService importEmbed,
        ILoggerFactory loggerFactory) =>
    {
        var logger = loggerFactory.CreateLogger("DevRagImportWeb");
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(ctx.Request.Body);

        var query = body.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var tag = body.TryGetProperty("tag", out var t) ? t.GetString() : null;
        var taskId = body.TryGetProperty("task_id", out var tid) ? tid.GetString() ?? "global" : "global";
        var maxPages = body.TryGetProperty("max_pages", out var mp) ? mp.GetInt32() : 5;
        var chunkSize = body.TryGetProperty("chunk_size", out var cs) ? cs.GetInt32() : 1000;
        var chunkOverlap = body.TryGetProperty("chunk_overlap", out var co) ? co.GetInt32() : 100;

        List<string>? urls = null;
        if (body.TryGetProperty("urls", out var urlsEl) && urlsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            urls = new List<string>();
            foreach (var u in urlsEl.EnumerateArray())
            {
                var urlStr = u.GetString();
                if (!string.IsNullOrWhiteSpace(urlStr)) urls.Add(urlStr);
            }
        }

        if (string.IsNullOrWhiteSpace(query) && (urls == null || urls.Count == 0))
            return Results.BadRequest(new { error = "query or urls is required" });

        var request = new Broker.Scripts.RagIngestService.WebSearchRequest
        {
            Query = query,
            Tag = tag ?? query,
            MaxPages = maxPages,
            Urls = urls,
            ChunkSize = chunkSize,
            ChunkOverlap = chunkOverlap
        };

        var result = await Broker.Scripts.RagIngestService.ImportFromWebAsync(
            request, taskId, importDb, importEmbed, logger);

        return Results.Ok(new
        {
            pages_fetched = result.PagesFetched,
            inserted = result.Inserted, skipped = result.Skipped,
            embedded = result.Embedded, errors = result.Errors
        });
    });

}

// ── Dev LINE Chat Gateway 端點 ──
{
    var gateway = app.Services.GetRequiredService<Broker.Services.LineChatGateway>();

    // POST /dev/line-chat — LINE Worker 呼叫此端點
    app.MapPost("/dev/line-chat", async (HttpContext ctx) =>
    {
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(ctx.Request.Body);
        var userId = body.TryGetProperty("user_id", out var u) ? u.GetString() ?? "" : "";
        var message = body.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(message))
            return Results.BadRequest(new { error = "user_id and message are required" });

        var result = await gateway.ChatAsync(userId, message, ctx.RequestAborted);
        return Results.Ok(new
        {
            reply = result.Reply,
            error = result.Error,
            rag_snippets = result.RagSnippets?.Select(s => new { s.Key, s.Content, s.Score }),
            history_count = result.HistoryCount
        });
    });

    // GET /dev/conversations — 列出所有對話
    app.MapGet("/dev/conversations", () =>
    {
        var conversations = gateway.ListConversations();
        return Results.Ok(new { conversations, total = conversations.Count });
    });

    // GET /dev/conversations/{userId} — 取得特定使用者對話
    app.MapGet("/dev/conversations/{userId}", (string userId) =>
    {
        var messages = gateway.GetConversation(userId);
        return Results.Ok(new { user_id = userId, messages, total = messages.Count });
    });

    app.MapDelete("/dev/conversations/{userId}", (string userId) =>
    {
        gateway.ClearConversation(userId);
        return Results.Ok(new { success = true, message = $"Conversation cleared for {userId}" });
    });

    // GET /dev/system/status — 系統狀態
    app.MapGet("/dev/system/status", async (
        BrokerCore.Data.BrokerDb statusDb,
        BrokerCore.Services.EmbeddingService statusEmbed,
        ILlmProxyService statusLlm) =>
    {
        // Ollama 可用性
        bool ollamaOk = false;
        string ollamaError = "";
        try { ollamaOk = await statusLlm.HealthCheckAsync(); }
        catch (Exception ex) { ollamaError = ex.Message; }

        // DB 統計
        var articleCount = statusDb.GetAll<BrokerCore.Models.SharedContextEntry>().Count();
        var vectorCount = statusDb.GetAll<BrokerCore.Models.VectorEntry>().Count();
        var convCount = statusDb.Query<int>(
            "SELECT COUNT(DISTINCT key) FROM shared_context_entries WHERE key LIKE 'convlog:%'").FirstOrDefault();

        return Results.Ok(new
        {
            status = ollamaOk ? "ok" : "degraded",
            timestamp = DateTime.UtcNow,
            services = new
            {
                llm = new { ok = ollamaOk, error = ollamaError, model = llmProxyOptions.DefaultModel, provider = llmProxyOptions.Provider },
                embedding = new { ok = statusEmbed.IsEnabled, model = embeddingConfig.Model },
                rag_pipeline = new { query_rewrite = ragPipelineConfig.QueryRewriteEnabled, rerank = ragPipelineConfig.RerankEnabled, cache = ragPipelineConfig.CacheEnabled }
            },
            database = new
            {
                shared_context_entries = articleCount,
                vector_entries = vectorCount,
                active_conversations = convCount
            }
        });
    });
}

// 業務端點
TaskEndpoints.Map(api);
SessionEndpoints.Map(api);
ExecutionEndpoints.Map(api);
UserApprovalEndpoints.Map(api);
CapabilityEndpoints.Map(api);
ToolSpecEndpoints.Map(api);
BrowserBindingEndpoints.Map(api);
DeploymentAdminEndpoints.Map(api);
AuditEndpoints.Map(api);
AdminEndpoints.Map(api);
ContextEndpoints.Map(api);
PlanEndpoints.Map(api);
RuntimeEndpoints.Map(api);
HighLevelEndpoints.Map(api);
GoogleDriveOAuthEndpoints.Map(api);
ArtifactDownloadEndpoints.Map(api);
PortalEndpoints.Map(api);
LocalAdminEndpoints.Map(api);
AgentEndpoints.Map(api);
// [新增 2026-10 AnthonyLee] 異常偵測報告的管理員 API；依賴的服務一律註冊，不必 gate 在 poolEnabled。
IncidentReportEndpoints.Map(api);
// [新增 2026-10 AnthonyLee] 同一份觀測資料（健康分數、worker、LLM 統計、異常報告）照 local-admin 規格再開一組，
// 管理後台用 cookie 工作階段就叫得到；pool 相關服務在 handler 內取用，不必 gate 在 poolEnabled。
LocalAdminObservabilityEndpoints.Map(api);
// [MONITORING EXTRACTION] health/worker endpoints 的 handler 依賴 IWorkerRegistry /
// HealthScoreService 等 FunctionPool 服務。FunctionPool 關閉時這些服務不註冊,若 endpoint
// 仍註冊,Minimal API 會把未註冊的服務參數推斷成 body,GET 端點 metadata inference 即拋
// "Body was inferred...";因此 endpoint 註冊本身也必須 gate 在 poolEnabled。
if (poolEnabled)
{
    HealthCheckEndpoints.Map(api);
    WorkerEndpoints.Map(api);
}

// ── Phase 3: 啟動功能池 TCP Listener ──
if (poolEnabled)
{
    var poolListener = app.Services.GetRequiredService<PoolListener>();
    await poolListener.StartAsync();

    var healthMonitor = app.Services.GetRequiredService<WorkerHealthMonitor>();
    healthMonitor.Start();

    // 優雅關閉
    // L-8 修復：Register 只接受 Action（非 Func<Task>），sync-over-async 在 shutdown hook 不可避免
    // 加入 10 秒 timeout 防止 shutdown 卡住（無 SynchronizationContext，不會 deadlock）
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        healthMonitor.Dispose();
        if (!poolListener.StopAsync().Wait(TimeSpan.FromSeconds(10)))
        {
            // StopAsync 超時，強制繼續 shutdown 流程
        }
    });
}

// ── RAG 種子（消費者保護法，僅首次執行） ──
if (builder.Configuration.GetValue("RagSeed:Enabled", true))
{
    using var scope = app.Services.CreateScope();
    var ragDb = scope.ServiceProvider.GetRequiredService<BrokerCore.Data.BrokerDb>();
    var ragEmbed = scope.ServiceProvider.GetRequiredService<BrokerCore.Services.EmbeddingService>();
    var ragLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("RAGSeed");

    var existingArticles = ragDb.GetAll<BrokerCore.Models.SharedContextEntry>()
        .Where(e => e.Key.StartsWith("消費者保護法:")).ToList();
    var existingVectors = ragDb.GetAll<BrokerCore.Models.VectorEntry>()
        .Where(v =>
            v.SourceKey.StartsWith("消費者保護法:") &&
            string.Equals(v.EmbeddingModel, ragEmbed.ModelName, StringComparison.Ordinal))
        .Count();

    if (existingArticles.Count == 0)
    {
        ragLogger.LogInformation("No consumer protection law data found, seeding...");
        var ragResult = Broker.Scripts.SeedConsumerProtectionLaw
            .SeedAsync(ragDb, ragEmbed, ragLogger).GetAwaiter().GetResult();
        ragLogger.LogInformation(
            "RAG seed done: {Inserted} inserted, {Embedded} embedded, errors: {Errors}",
            ragResult.Inserted, ragResult.Embedded, ragResult.Errors.Count);
    }
    else if (existingVectors < existingArticles.Count && ragEmbed.IsEnabled)
    {
        // 條目已寫入但向量尚未產生（上次 embedding model 未就緒）
        ragLogger.LogInformation(
            "Found {Articles} articles but only {Vectors} vectors, backfilling embeddings...",
            existingArticles.Count, existingVectors);

        var backfilled = 0;
        // 取每個 key 的最新版本
        var latestByKey = existingArticles
            .GroupBy(e => e.Key)
            .Select(g => g.OrderByDescending(e => e.Version).First())
            .ToList();

        foreach (var entry in latestByKey)
        {
            var hasVec = ragDb.GetAll<BrokerCore.Models.VectorEntry>()
                .Any(v =>
                    v.SourceKey == entry.Key &&
                    v.TaskId == (entry.TaskId ?? "global") &&
                    string.Equals(v.EmbeddingModel, ragEmbed.ModelName, StringComparison.Ordinal));
            if (hasVec) continue;

            var hash = BrokerCore.Services.EmbeddingService.ComputeHash(entry.ContentRef);
            var vector = ragEmbed.EmbedAsync(entry.ContentRef).GetAwaiter().GetResult();
            if (vector == null) continue;

            ragDb.Insert(new BrokerCore.Models.VectorEntry
            {
                EntryId = BrokerCore.IdGen.New("vec"),
                SourceKey = entry.Key,
                TaskId = entry.TaskId ?? "global",
                TextPreview = entry.ContentRef.Length > 500 ? entry.ContentRef[..500] : entry.ContentRef,
                ContentHash = hash,
                Embedding = BrokerCore.Services.EmbeddingService.VectorToBytes(vector),
                EmbeddingModel = ragEmbed.ModelName,
                Dimension = vector.Length,
                CreatedAt = DateTime.UtcNow
            });
            backfilled++;
        }

        ragLogger.LogInformation("Backfilled {Count} embeddings", backfilled);
    }
    else
    {
        ragLogger.LogInformation(
            "Consumer protection law: {Articles} articles, {Vectors} vectors — up to date.",
            existingArticles.Count, existingVectors);
    }

    // FTS5 回補：確保 memory_fts 有正確分詞的資料
    try
    {
        var latestEntries = existingArticles
            .GroupBy(e => e.Key)
            .Select(g => g.OrderByDescending(e => e.Version).First())
            .ToList();

        // 檢查是否需要重建：隨機取一筆看內容是否已分詞（CJK 字元間有空格）
        var needsRebuild = false;
        var ftsCount = ragDb.Query<int>("SELECT COUNT(*) FROM memory_fts WHERE task_id = @taskId",
            new { taskId = "global" }).FirstOrDefault();

        if (ftsCount < latestEntries.Count)
        {
            needsRebuild = true;
        }
        else if (ftsCount > 0)
        {
            // 取一筆看看內容是否已有 CJK 分詞空格
            var sample = ragDb.Query<RagFtsResult>(
                "SELECT content FROM memory_fts WHERE task_id = @taskId LIMIT 1",
                new { taskId = "global" }).FirstOrDefault();
            if (sample?.Content != null)
            {
                // 若內容含連續 CJK 字元（無空格分隔），則需要重建
                bool hasConsecutiveCjk = false;
                bool prevWasCjk = false;
                foreach (var ch in sample.Content)
                {
                    bool isCjk = char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.OtherLetter;
                    if (isCjk && prevWasCjk) { hasConsecutiveCjk = true; break; }
                    prevWasCjk = isCjk;
                }
                needsRebuild = hasConsecutiveCjk;
            }
        }

        if (needsRebuild)
        {
            ragLogger.LogInformation(
                "FTS5 index needs rebuild (CJK tokenization fix), rebuilding {Count} entries...",
                latestEntries.Count);

            // 清空現有 global task 的 FTS5 資料
            ragDb.Execute("DELETE FROM memory_fts WHERE task_id = @taskId", new { taskId = "global" });

            var ftsBackfilled = 0;
            foreach (var entry in latestEntries)
            {
                try
                {
                    ragDb.Execute(
                        "INSERT INTO memory_fts(source_key, content, task_id) VALUES(@key, @value, @taskId)",
                        new { key = entry.Key,
                              value = BrokerCore.Services.Fts5TextNormalizer.PrepareContent(entry.ContentRef),
                              taskId = entry.TaskId ?? "global" });
                    ftsBackfilled++;
                }
                catch { }
            }
            ragLogger.LogInformation("FTS5 rebuilt {Count} entries with CJK tokenization", ftsBackfilled);
        }
    }
    catch (Exception ex)
    {
        ragLogger.LogWarning(ex, "FTS5 rebuild check failed");
    }
}

app.Run();

// 種子密鑰曾被管理員撤銷時不會重新種入（撤銷跨重啟保留）；提醒操作者要恢復須更換密鑰。
static void WarnIfSeedCredentialHeldRevoked(BrokerDbInitializer initializer, string section, ILogger logger)
{
    if (initializer.SeedCredentialHeldRevoked)
    {
        logger.LogWarning(
            "The {Section} registration credential was revoked by an operator and is not seeded again; rotate its RegistrationSecret to allow registration.",
            section);
    }
}

// Dev-only FTS result DTO
class RagFtsResult
{
    [BaseOrm.Column("source_key")]
    public string? SourceKey { get; set; }
    [BaseOrm.Column("content")]
    public string? Content { get; set; }
    [BaseOrm.Column("rank")]
    public double Rank { get; set; }
}

// Marker class for WebApplicationFactory<Program> in integration tests
public partial class Program { }
