using BrokerCore.Crypto;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;
using Broker.Adapters;
using Broker.Endpoints;
using Broker.Middleware;
using Broker.Services;
using FunctionPool.Container;
using FunctionPool.ContainerLogs;
using FunctionPool.Diagnostics;
using FunctionPool.Dispatch;
using FunctionPool.Health;
using FunctionPool.Models;
using FunctionPool.Network;
using FunctionPool.Registry;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);

// ── 共用 LoggerFactory（修復 M-7：消除冗餘 LoggerFactory 實例） ──
using var startupLoggerFactory = LoggerFactory.Create(b => b.AddConsole());
var startupLogger = startupLoggerFactory.CreateLogger("Program");

// ── 資料庫 ──
var dbPath = builder.Configuration.GetValue<string>("Database:Path") ?? "broker.db";
var connectionString = $"Data Source={dbPath}";
builder.Services.AddSingleton(sp => BrokerDb.UseSqlite(connectionString));

// ── 初始化資料庫（17 張表 + 種子資料） ──
using (var initDb = BrokerDb.UseSqlite(connectionString))
{
    var initializer = new BrokerDbInitializer(initDb);
    var developmentSeed = builder.Configuration.GetSection("DevelopmentSeed").Get<DevelopmentSeedOptions>();
    initializer.Initialize(developmentSeed);
    // Dashboard 種子（管理介面專用 Principal）
    var dashboardSeed = builder.Configuration.GetSection("DashboardSeed").Get<DevelopmentSeedOptions>();
    if (dashboardSeed?.Enabled == true)
        initializer.Initialize(dashboardSeed);

    // Agent Inbox 表（MVP-1 2026-05-01）— 不動 BrokerDbInitializer.cs（Benson 的）。
    // EnsureTable 是 idempotent，補在初始化區段尾巴最乾淨。
    initDb.EnsureTable<AgentInboxTask>();
    // Auto-trader 監控清單持久化（2026-05-02）— 取代 ConcurrentDictionary in-memory 設計
    initDb.EnsureTable<AutoTradeWatchEntry>();
    initDb.EnsureTable<RiskAnchorState>();
    initDb.EnsureTable<LlmReasoningAuditEntry>();
    // Auto-trader 全域設定 + 永續部位保護狀態（2026-05-08 補完）
    // 設定：enabled / interval_seconds 重啟保留；perp 部位狀態：SL / peak / be_moved 重啟保留
    initDb.EnsureTable<AutoTraderSettingsEntry>();
    initDb.EnsureTable<PerpetualPositionStateEntry>();
    // H3 — Approval template（命中規則自動 approve、降低 approval 疲勞）
    initDb.EnsureTable<Broker.Models.ApprovalTemplate>();
    // H2 — Time-windowed ACL rule
    initDb.EnsureTable<Broker.Models.TimeAclRule>();
    // I1 — Multi-sig rules + per-approver decisions
    initDb.EnsureTable<Broker.Models.MultiSigRule>();
    initDb.EnsureTable<Broker.Models.ApprovalDecisionRecord>();
    // Strategy Lab 自動回測（2026-05-09）— 每日批次跑、結果存 DB、API 查推薦
    initDb.EnsureTable<BacktestRunEntry>();
    initDb.EnsureTable<BacktestResultEntry>();
    // Principal 多用戶帳密 + cookie session（Phase A1 2026-05-10）
    initDb.EnsureTable<PrincipalCredential>();
    initDb.EnsureTable<PrincipalSession>();
    // 用戶交易所 API 憑證（Phase A2.5a 2026-05-10）— at-rest AES-GCM 加密、AAD 綁 entry_id
    initDb.EnsureTable<ExchangeCredential>();
    // 多用戶推播頻道（2026-06-02）— 每個 principal 自己的 Discord webhook / LINE,target at-rest 加密
    initDb.EnsureTable<NotificationChannel>();
    // Portfolio Scanner Hybrid (2026-05-27 Phase 1) — scanner 定義 + 已開 active legs
    // 核心腿是固定 (策略, 幣);scanner 是「策略 + 候選幣池」、AutoTrader 每 cycle 挑訊號最強的開
    initDb.EnsureTable<ScannerLegEntry>();
    initDb.EnsureTable<ScannerActiveLegEntry>();
    // 對既有 DB 補欄位（mode / leverage 是 Phase 3 加的、舊表沒有）
    Broker.Services.AutoTraderDbMigrations.Apply(initDb, startupLoggerFactory.CreateLogger("AutoTraderDbMigrations"));
    // Alert system（#2 2026-05-07）—— 規則 + 事件
    initDb.EnsureTable<AlertRuleEntry>();
    initDb.EnsureTable<AlertEventEntry>();
    // Notification dedup（5/19 修：broker rebuild 重啟後 in-memory dedup state 丟失、
    // 同樣 risk-blocked 訊息被連環推 9 次到 Discord/LINE。改持久化、跨重啟記得 30 min 內已推過）
    initDb.EnsureTable<NotificationDedupEntry>();
    // 台股每日資金流（2026-06-04）— 三大法人買賣超 + 融資融券、累積歷史供研究 + 每日 digest 推 DM
    initDb.EnsureTable<TwFundFlowDaily>();
}

// ── Step 2: 加密基礎建設 ──
// ECDH P-256 金鑰對（Singleton，所有 instance 共享同一金鑰）
var ecdhPrivateKey = builder.Configuration.GetSecret("Broker:Encryption:EcdhPrivateKeyBase64");
if (!string.IsNullOrEmpty(ecdhPrivateKey) && !ecdhPrivateKey.StartsWith("CHANGE_ME"))
{
    builder.Services.AddSingleton<IEnvelopeCrypto>(sp =>
        new EnvelopeCrypto(ecdhPrivateKey));
}
else
{
    // 開發模式：自動生成金鑰對（每次重啟不同）
    var crypto = new EnvelopeCrypto();
    builder.Services.AddSingleton<IEnvelopeCrypto>(crypto);
    // M-3 修復：不再將完整金鑰記錄到 log（即使是 public key 也不應洩漏至 log aggregation）
    startupLogger.LogWarning(
        "Using auto-generated ECDH key pair. Set Broker:Encryption:EcdhPrivateKeyBase64 in production.");
    var pubKey = crypto.GetBrokerPublicKey();
    startupLogger.LogInformation(
        "Broker public key generated (length={KeyLength}, prefix={KeyPrefix}...)",
        pubKey.Length, pubKey[..Math.Min(8, pubKey.Length)]);
}

// 啟動時 log 機密來源總覽——讓人知道哪些 key 從 file mount、哪些從 env、哪些缺
builder.Configuration.LogSecretSummary(startupLogger,
    "Broker:Encryption:EcdhPrivateKeyBase64",
    "Broker:Encryption:MasterKeyBase64",
    "Broker:ScopedToken:Secret",
    "LlmProxy:ApiKey");

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
var masterKeyBase64 = builder.Configuration.GetSecret("Broker:Encryption:MasterKeyBase64") ?? "";
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
var tokenSecret = builder.Configuration.GetSecret("Broker:ScopedToken:Secret") ?? "";
var tokenIssuer = builder.Configuration.GetValue<string>("Broker:ScopedToken:Issuer") ?? "broker-control-plane";
var tokenAudience = builder.Configuration.GetValue<string>("Broker:ScopedToken:Audience") ?? "broker-agents";
var tokenExpMin = builder.Configuration.GetValue<int>("Broker:ScopedToken:ExpirationMinutes");
if (tokenExpMin <= 0) tokenExpMin = 15;

builder.Services.AddSingleton<IScopedTokenService>(sp =>
    new ScopedTokenService(tokenSecret, tokenIssuer, tokenAudience, tokenExpMin));

builder.Services.AddSingleton<ISessionService>(sp =>
    new SessionService(sp.GetRequiredService<BrokerDb>()));

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
// G2 — AuditEventBus + BroadcastingAuditService 包在 Benson 的 AuditService 外層、
// 所有 IAuditService caller 自動走廣播。Benson 原作（broker-core/AuditService.cs）一行不改。
builder.Services.AddSingleton<Broker.Services.AuditEventBus>();
builder.Services.AddSingleton<IAuditService>(sp =>
{
    var inner = new AuditService(sp.GetRequiredService<BrokerDb>());
    var bus = sp.GetRequiredService<Broker.Services.AuditEventBus>();
    var logger = sp.GetRequiredService<ILogger<Broker.Services.BroadcastingAuditService>>();
    return new Broker.Services.BroadcastingAuditService(inner, bus, logger);
});

// ── Step 4.4.5: Shutdown 旗標（給 PoolDispatcher 拒絕 broker 關閉中的新派發） ──
builder.Services.AddSingleton<IShutdownState, ShutdownState>();

// W14 P1+P5：緊急停機 / 唯讀鎖定旗標
builder.Services.AddSingleton<Broker.Services.IEmergencyState, Broker.Services.EmergencyStateService>();
// W14 P2：bot 呼叫速率限制（per-Discord-user sliding window）
builder.Services.AddSingleton<Broker.Services.BotRateLimitService>();

// ── Step 4.5: Capability ACL（PoolDispatcher 派發前查 role + principal override） ──
builder.Services.AddSingleton<ICapabilityAclService>(sp =>
    new CapabilityAclService(sp.GetRequiredService<BrokerDb>()));

// ── Step 4.6: Worker 健康綜合分數（heartbeat + dispatch success + resource） ──
builder.Services.AddSingleton<Broker.Services.HealthScoreService>();

// ── Step 4.6b: 每 5 min 拍 health snapshot 進 DB（給 dashboard 趨勢圖） ──
builder.Services.AddSingleton<Broker.Services.HealthScoreSnapshotService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.HealthScoreSnapshotService>());

// ── 自動移轉階段①:叢集選主(唯讀)。設 Cluster:EtcdEndpoints 才啟用真選主;否則單機永遠 PRIMARY(現狀不變) ──
var clusterEtcd = builder.Configuration.GetValue<string>("Cluster:EtcdEndpoints");
if (!string.IsNullOrWhiteSpace(clusterEtcd))
{
    var clusterNodeId = builder.Configuration.GetValue<string>("Cluster:NodeId") ?? Environment.MachineName;
    var clusterLeaseTtl = builder.Configuration.GetValue<int>("Cluster:LeaseTtlSeconds", 10);
    var clusterEps = clusterEtcd.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    builder.Services.AddSingleton<Broker.Services.EtcdLeaderElection>(sp =>
        new Broker.Services.EtcdLeaderElection(clusterEps, clusterNodeId, clusterLeaseTtl,
            sp.GetRequiredService<ILogger<Broker.Services.EtcdLeaderElection>>()));
    builder.Services.AddSingleton<Broker.Services.ILeaderElection>(sp => sp.GetRequiredService<Broker.Services.EtcdLeaderElection>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.EtcdLeaderElection>());
}
else
{
    builder.Services.AddSingleton<Broker.Services.ILeaderElection, Broker.Services.SingleNodeLeaderElection>();
}
// 階段②:LeaderGuard —— 讓「只該由 PRIMARY 做」的非真錢週期工作在 STANDBY 自我跳過(單機永遠跑、現狀不變)
builder.Services.AddSingleton<Broker.Services.LeaderGuard>();

// ── Step 4.6c: 治理層告警（health 變壞 / 新 pending approval → Discord + LINE 推送） ──
builder.Services.AddHostedService<Broker.Services.GovernanceAlertsService>();

// ── Step 4.7: Approve-before-execute（高風險 capability 需 admin 點按 approve） ──
// H3 — Approval template matcher（在 ITradingApprovalService 註冊前先註冊、底下 decorator 會用到）
builder.Services.AddSingleton<Broker.Services.ApprovalTemplateMatcher>();
// H2 — Time-windowed ACL service
builder.Services.AddSingleton<Broker.Services.TimeAclService>();

// 四層裝飾鏈：MultiSig → Time → Template → 原 TradingApprovalService
builder.Services.AddSingleton<ITradingApprovalService>(sp =>
{
    var inner = new TradingApprovalService(sp.GetRequiredService<BrokerDb>());
    var templateAware = new Broker.Services.TemplateAwareApprovalService(
        inner,
        sp.GetRequiredService<Broker.Services.ApprovalTemplateMatcher>(),
        sp.GetRequiredService<IAuditService>(),
        sp.GetRequiredService<ILogger<Broker.Services.TemplateAwareApprovalService>>());
    var timeAware = new Broker.Services.TimeAwareApprovalService(
        templateAware,
        sp.GetRequiredService<Broker.Services.TimeAclService>(),
        sp.GetRequiredService<ILogger<Broker.Services.TimeAwareApprovalService>>());
    return new Broker.Services.MultiSigApprovalService(
        timeAware,
        sp.GetRequiredService<BrokerDb>(),
        sp.GetRequiredService<IAuditService>(),
        sp.GetRequiredService<ILogger<Broker.Services.MultiSigApprovalService>>());
});

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
// Docker secrets 支援：若設了 LlmProxy:ApiKeyFile，覆蓋從 binder 拿到的 ApiKey（可能為空）
var llmApiKeyFromSecret = builder.Configuration.GetSecret("LlmProxy:ApiKey");
if (!string.IsNullOrEmpty(llmApiKeyFromSecret))
    llmProxyOptions.ApiKey = llmApiKeyFromSecret;
builder.Services.AddSingleton(llmProxyOptions);
builder.Services.AddSingleton<LlmProxyMetrics>();
// HttpClient + 內部 LlmProxyService 註冊在 keyed name "raw"、外面 Metered → Quota 雙裝飾器包起來
builder.Services.AddHttpClient<LlmProxyService>();
// H1 — per-principal LLM token quota（in-memory、daily reset、預設 soft mode）
builder.Services.AddSingleton<Broker.Services.IPrincipalQuotaService, Broker.Services.PrincipalQuotaService>();
builder.Services.AddSingleton<ILlmProxyService>(sp =>
{
    var metered = new MeteredLlmProxyService(
        sp.GetRequiredService<LlmProxyService>(),
        sp.GetRequiredService<LlmProxyMetrics>());
    return new Broker.Services.QuotaEnforcedLlmProxyService(
        metered,
        sp.GetRequiredService<Broker.Services.IPrincipalQuotaService>(),
        sp.GetRequiredService<ILogger<Broker.Services.QuotaEnforcedLlmProxyService>>());
});
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
builder.Services.AddSingleton<Broker.Services.ProjectInterviewStateService>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewRestatementService>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewStateMachine>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewTemplateCatalogService>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewProjectDefinitionCompiler>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewWorkflowDesignService>();
builder.Services.AddSingleton<Broker.Services.ProjectInterviewPdfRenderService>();
builder.Services.AddSingleton<Broker.Services.HighLevelLineWorkspaceService>();
// Artifact 下載 API(2026-05-29 AnthonyLee — 執行 Benson 2026-03-29 plan 的新檔;reuse 不改原檔)
builder.Services.AddSingleton(new Broker.Services.BrokerArtifactDownloadOptions
{
    SigningSecret = builder.Configuration["ArtifactDownload:SigningSecret"]
        ?? Environment.GetEnvironmentVariable("ARTIFACT_DOWNLOAD_SIGNING_SECRET") ?? "",
    TtlSeconds = int.TryParse(builder.Configuration["ArtifactDownload:TtlSeconds"], out var _artDlTtl) ? _artDlTtl : 3600,
    PublicBaseUrl = builder.Configuration["ArtifactDownload:PublicBaseUrl"] ?? "",
});
builder.Services.AddSingleton<Broker.Services.BrokerArtifactDownloadService>();
builder.Services.AddSingleton<Broker.Services.LineArtifactDeliveryService>();
builder.Services.AddSingleton<Broker.Services.HighLevelSystemScaffoldSpecStore>();
builder.Services.AddSingleton<Broker.Services.HighLevelSystemScaffoldIterationStore>();
builder.Services.AddSingleton<Broker.Services.HighLevelSystemScaffoldProgressStore>();
builder.Services.AddSingleton<Broker.Services.HighLevelDocumentArtifactService>();
builder.Services.AddSingleton<Broker.Services.HighLevelCodeArtifactService>();
builder.Services.AddSingleton<Broker.Services.HighLevelSystemScaffoldService>();
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
// D1 Phase 1 — sizing service 必須在 AutoTraderService 前註冊（後者依賴它）
builder.Services.AddSingleton<Broker.Services.AutoTraderSizingService>();
builder.Services.AddSingleton<Broker.Services.AutoTraderService>();
builder.Services.AddSingleton<Broker.Services.PortfolioSizingService>();   // Q1.6 portfolio sizing 推薦
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.AutoTraderService>());
builder.Services.AddSingleton<Broker.Services.PriceAlertService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.PriceAlertService>());
builder.Services.AddSingleton<Broker.Services.AlertRulesService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.AlertRulesService>());
builder.Services.AddSingleton<Broker.Services.SymbolScreenerService>();
builder.Services.AddSingleton<Broker.Services.ScheduledBacktestService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.ScheduledBacktestService>());
builder.Services.AddSingleton<Broker.Services.PrincipalAuthService>();
builder.Services.AddSingleton<BrokerCore.Crypto.AtRestSecretCrypto>(sp =>
{
    // 重用 broker master key 做 at-rest 加密（跟 ECDH session 加密同 key、不同 AAD）。
    var cfg = sp.GetRequiredService<IConfiguration>();
    var key = cfg.GetSecret("Broker:Encryption:MasterKeyBase64") ?? "";
    return new BrokerCore.Crypto.AtRestSecretCrypto(key);
});
builder.Services.AddSingleton<Broker.Services.ExchangeCredentialService>();
builder.Services.AddSingleton<Broker.Services.NotificationChannelService>();
builder.Services.AddSingleton<Broker.Services.BacktestHistoryService>();
builder.Services.AddSingleton<Broker.Services.PortfolioAnalyticsService>();
builder.Services.AddSingleton<Broker.Services.BenchmarkService>();
builder.Services.AddSingleton<Broker.Services.StrategyComparisonService>();
builder.Services.AddSingleton<Broker.Services.StrategyCandidateRepository>();
builder.Services.AddSingleton<Broker.Services.StrategyGeneratorService>();
builder.Services.AddSingleton<Broker.Services.StrategyResearchLoopService>();
builder.Services.AddSingleton<Broker.Services.KellyPositionSizingService>();
builder.Services.AddHttpClient("discord-webhook");
// Notification dedup repo（5/19、防 broker rebuild 後相同 risk-blocked 訊息重推 N 次）
// 必須在 Discord / LineNotificationService 之前註冊（後者依賴它）
builder.Services.AddSingleton<Broker.Services.NotificationDedupRepo>();
builder.Services.AddSingleton<Broker.Services.DiscordNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.DiscordNotificationService>());
builder.Services.AddSingleton<Broker.Services.LineNotificationService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.LineNotificationService>());
// 每日交易彙整、UTC 00:00（或 DAILY_REPORT_AT_UTC_HOUR）推 Discord + LINE
// Singleton 版本給 manual trigger endpoint 用、HostedService 共用同一個 instance（避免兩份狀態）
builder.Services.AddSingleton<Broker.Services.DailyReportService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.DailyReportService>());
// 台股資金流每日彙整（2026-06-04）— 抓三大法人+融資融券、存 DB、TW_FUNDFLOW_AT_UTC_HOUR(預設9=17:00 TST)推 DM
// Singleton 給手動 endpoint 用、HostedService 共用同一 instance
builder.Services.AddSingleton<Broker.Services.TwFundFlowService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.TwFundFlowService>());
// AutoTrader 心跳看門狗 — sweep 卡住(真錢無人看管)就推 Discord/LINE 告警、恢復再推一則
builder.Services.AddHostedService<Broker.Services.AutoTraderHeartbeatService>();
// ETH/BTC 比值告警 — 「ETH 補漲論」的客觀觸發點(突破 0.030 推、跌破 0.025 推)
builder.Services.AddHostedService<Broker.Services.EthBtcRatioAlertService>();
// 橫斷面動量 shadow runner — portfolio-level、不下單、forward 驗證 xsmom edge(XSMOM_SHADOW_ENABLED=true 開)
builder.Services.AddHostedService<Broker.Services.XsMomShadowService>();
// 動態合約規格 cache，啟動 + 每 12h 從 trading-worker 拉、灌進 BrokerCore.Trading.SymbolSpecs
builder.Services.AddSingleton<Broker.Services.SymbolSpecsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.SymbolSpecsService>());
// 偵測 user 主動劃轉/充值/提領、自動更新 risk anchor（5 min interval / 5 USDT threshold）
builder.Services.AddSingleton<Broker.Services.BalanceAnchorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.BalanceAnchorService>());
// 偵測表現差的策略 watch、連虧 5 筆或 win_rate<30% 自動 pause + 通知（self-healing）
builder.Services.AddSingleton<Broker.Services.StrategyHealthMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.StrategyHealthMonitor>());
// 3 條 agent_inbox-driven BackgroundService、共用 ScheduledForensicsAgentBase 基底、
// 走 broker LlmProxy 走 MeteredLlmProxyService → 完全對齊 Benson broker-as-control-plane
builder.Services.AddHostedService<Broker.Services.ForensicsAgentService>();       // hourly  / 1h window — 事件鏈鑑識
builder.Services.AddHostedService<Broker.Services.MarketReportAgentService>();    // 6-hour / 6h window — 營運總結
builder.Services.AddHostedService<Broker.Services.AnomalyDetectorAgentService>(); // 15min  / 15m window — 異常偵測
// Manual-push only：策略研究 agent — push 一筆 JSON 就跑 StrategyResearchLoopService（LLM 生成 + walk-forward 回測迭代）
builder.Services.AddHostedService<Broker.Services.StrategyResearchAgentService>();
// Daily 24h auto — 帳戶 + 監控 + 持倉 snapshot 組成市場日報
builder.Services.AddHostedService<Broker.Services.DailyMarketReportAgentService>();
// Every 6h auto — 掃 watch list 健康度、列異常
builder.Services.AddHostedService<Broker.Services.StrategyHealthInspectorAgentService>();
// Manual-push only — 風控壓測、模擬 mark price 跌 X% 看每倉是否觸發 SL/Liq
builder.Services.AddHostedService<Broker.Services.RiskStressTestAgentService>();
// 30 min auto — 驗證 Benson 設計的 audit_events hash chain 完整性（防篡改、純規則檢查無 LLM）
builder.Services.AddHostedService<Broker.Services.AuditChainVerifierAgentService>();
// E1 1h auto — KPI bar 數值 snapshot、未來可拉歷史畫趨勢
builder.Services.AddHostedService<Broker.Services.DashboardSnapshotAgentService>();
// E2 6h auto — capability 使用統計（補 LLM 全景看不到的 dispatch 維度）
builder.Services.AddHostedService<Broker.Services.CapabilityUsageReporterAgentService>();
// E3 manual — 給 symbol+time 寫該 trade post-mortem
builder.Services.AddHostedService<Broker.Services.TradeJournalAgentService>();
// E4 manual — 給 query/urls 走 RAG ingest 進 vector_entries
builder.Services.AddHostedService<Broker.Services.RagIngestAgentService>();
// E5 7d auto — 週級事件總結
builder.Services.AddHostedService<Broker.Services.WeeklyDigestAgentService>();
// W14 P4 1h auto — File Integrity Monitor（hash sensitive files、變動寫 audit + dashboard 警告）
builder.Services.AddHostedService<Broker.Services.FileIntegrityAgentService>();

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
                sp.GetRequiredService<ILogger<PoolDispatcher>>(),
                sp.GetService<IAuditService>(),    // 讓 dashboard direct-dispatch 也能被 trace
                sp.GetService<ICapabilityAclService>(),   // role-based capability allowlist
                sp.GetService<ITradingApprovalService>(),        // approve-before-execute for sensitive caps
                sp.GetService<IShutdownState>());         // graceful shutdown gate
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
                sp.GetRequiredService<ILogger<PoolDispatcher>>(),
                sp.GetService<IAuditService>(),    // 讓 dashboard direct-dispatch 也能被 trace
                sp.GetService<ICapabilityAclService>(),   // role-based capability allowlist
                sp.GetService<ITradingApprovalService>(),        // approve-before-execute for sensitive caps
                sp.GetService<IShutdownState>());         // graceful shutdown gate
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
            BrokerPortForWorkers = builder.Configuration.GetValue("FunctionPool:ContainerManager:BrokerPortForWorkers", 7000)
        };

        // Load worker image configs from configuration
        var imageSection = builder.Configuration.GetSection("FunctionPool:ContainerManager:WorkerImages");
        foreach (var child in imageSection.GetChildren())
        {
            containerConfig.WorkerImages[child.Key] = new WorkerImageConfig
            {
                Image = child.GetValue<string>("Image") ?? $"bricks4agent/{child.Key}:latest",
                MemoryLimit = child.GetValue<string>("MemoryLimit"),
                CpuLimit = child.GetValue<string>("CpuLimit"),
                User = child.GetValue<string>("User")
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

        // 自動重啟：container 變 Stopped/Failed 時用 exponential backoff 重啟
        // 只在 ContainerManager 真的啟用時才註冊，避免 NoOpContainerManager 上跑無效循環
        builder.Services.AddSingleton<Broker.Services.WorkerAutoRestartService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.WorkerAutoRestartService>());

        // 自動擴縮：utilization 高 → spawn / 全閒 → stop oldest
        builder.Services.AddSingleton<Broker.Services.WorkerAutoScaleService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<Broker.Services.WorkerAutoScaleService>());

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

    builder.Services.AddSingleton<IDiagnosticsService>(sp =>
        new DiagnosticsService(
            sp.GetRequiredService<IContainerManager>(),
            sp.GetRequiredService<IWorkerRegistry>(),
            sp.GetRequiredService<ILogger<DiagnosticsService>>()));

    // 排程診斷服務（每 N 分鐘自動掃描，結果存 SQLite）
    var diagIntervalMin2 = builder.Configuration.GetValue("FunctionPool:ScheduledDiagnostics:IntervalMinutes", 15);
    var diagRetDays2     = builder.Configuration.GetValue("FunctionPool:ScheduledDiagnostics:RetentionDays", 7);
    var diagArcDays2     = builder.Configuration.GetValue("FunctionPool:ScheduledDiagnostics:ArchiveRetentionDays", 90);
    var diagDbPath2      = builder.Configuration.GetValue<string>("FunctionPool:ScheduledDiagnostics:DbPath")
                           ?? Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "diagnostics.db");
    builder.Services.AddSingleton<ScheduledDiagnosticsService>(sp =>
        new ScheduledDiagnosticsService(
            sp.GetRequiredService<IDiagnosticsService>(),
            sp.GetRequiredService<ILogger<ScheduledDiagnosticsService>>(),
            diagDbPath2, diagIntervalMin2, diagRetDays2, diagArcDays2));

    // 容器日誌採集服務（每 N 秒掃 running 容器，Error/Warn 寫入獨立 SQLite）
    var logTailEnabled = builder.Configuration.GetValue("FunctionPool:ContainerLogTail:Enabled",
                                                         containerEnabled);
    if (logTailEnabled && containerEnabled)
    {
        var logTailPoll    = builder.Configuration.GetValue("FunctionPool:ContainerLogTail:PollSeconds", 10);
        var logTailRetDays = builder.Configuration.GetValue("FunctionPool:ContainerLogTail:RetentionDays", 7);
        var logTailDbPath  = builder.Configuration.GetValue<string>("FunctionPool:ContainerLogTail:DbPath")
                             ?? Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "container-logs.db");
        builder.Services.AddSingleton<ContainerLogTailService>(sp =>
            new ContainerLogTailService(
                sp.GetRequiredService<IContainerManager>(),
                sp.GetRequiredService<ContainerConfig>(),
                sp.GetRequiredService<ILogger<ContainerLogTailService>>(),
                logTailDbPath, logTailPoll, logTailRetDays));
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
    new AgentSpawnService(sp.GetRequiredService<BrokerDb>()));

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
    if (!string.IsNullOrWhiteSpace(highLevelLlmOptions.ApiKey))
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

builder.Services.AddSingleton<IBrokerService>(sp =>
    new BrokerService(
        sp.GetRequiredService<BrokerDb>(),
        sp.GetRequiredService<IPolicyEngine>(),
        sp.GetRequiredService<IAuditService>(),
        sp.GetRequiredService<ICapabilityCatalog>(),
        sp.GetRequiredService<ISessionService>(),
        sp.GetRequiredService<IRevocationService>(),
        sp.GetRequiredService<ITaskRouter>(),
        sp.GetRequiredService<IExecutionDispatcher>(),
        sp.GetRequiredService<IToolSpecStatusChecker>()));

builder.Services.AddSingleton<IPlanEngine>(sp =>
    new PlanEngine(
        sp.GetRequiredService<IPlanService>(),
        sp.GetRequiredService<IBrokerService>(),
        sp.GetRequiredService<ISharedContextService>(),
        sp.GetRequiredService<IAuditService>(),
        sp.GetRequiredService<IObservationService>()));

var app = builder.Build();

app.Logger.LogInformation("Broker database path: {DbPath}", dbPath);

// Phase A1：seed 一筆 admin（prn_dashboard / 預設密碼 admin、強制下次改）
app.Services.GetRequiredService<Broker.Services.PrincipalAuthService>().EnsureInitialAdmin(app.Configuration);

// 組合對帳(config-as-code):依 portfolio.json 補齊缺的 shadow watch。
// 解析 AutoTraderService(其建構子已載入既有 watch)→ reconciler 只新增 shadow、不碰真錢/既有。
try
{
    Broker.Services.PortfolioReconciler.Apply(
        app.Services.GetRequiredService<Broker.Services.AutoTraderService>(),
        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PortfolioReconciler"));
}
catch (Exception ex) { app.Logger.LogWarning(ex, "PortfolioReconciler skipped"); }

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

// ── 靜態檔案（Dashboard UI）── 必須在加密/認證中間件之前
app.UseWebSockets();
// UseDefaultFiles 必須在 UseStaticFiles 之前；它會把 / 請求 rewrite 成 /index.html
// 讓使用者打 http://localhost:5100/ 直接看到 dashboard，不用記完整路徑
app.UseDefaultFiles();
app.UseStaticFiles();

// ── Middleware 管線（順序關鍵） ──
// [0] BodySizeLimitMiddleware（H-10 修復：防止 DoS 超大 payload）
var maxBodyBytes = builder.Configuration.GetValue<long>("Broker:MaxRequestBodyBytes", 1_048_576); // 1MB default
app.UseBodySizeLimit(maxBodyBytes);

// Internal bot auth：在 encryption / cookie 之前先比對 X-Internal-Bot-Token、設 identity。
// 沒帶 header 走原流程；帶錯 header 直接 401（不靜默 fall-through）。
app.UseInternalBotAuth();
// [1] ExceptionMiddleware（全域例外）— TODO: Phase 5
// [2] IpRateLimiter（限流）— TODO: Phase 5
// [3] EncryptionMiddleware（信封解密/加密）
app.UseEnvelopeEncryption();
// [4] WorkerIdentityAuthMiddleware（worker caller 驗證）
app.UseWorkerIdentityAuth();
// [5] BrokerAuthMiddleware（Token 驗證）
app.UseBrokerAuth();
// [6] AuditMiddleware（稽核記錄）
app.UseBrokerAudit();
// [7] CurrentUserMiddleware（Phase A2：把 cookie session 解出來、塞 HttpContext.Items 給 endpoint 用）
app.UseCurrentUser();
// [8] EmergencyGateMiddleware（W14 P1+P5：read-only / kill-switch 寫入閘）— 必須在 auth 之後、endpoint 之前
app.UseEmergencyGate();

// ── Dashboard（靜態 HTML，由 UseStaticFiles 提供） ──
// Dashboard JS 內建完整 ECDH+AES-GCM 加密客戶端，所有 API 呼叫走加密 POST

// ── API 路由 ──
var api = app.MapGroup("/api/v1");

// /metrics（Prometheus exposition format，根層、非 /api/v1）—— 給外部監控 scrape
Broker.Endpoints.MetricsEndpoints.Map(app);

// L-7 修復：健康檢查同時支援 GET（標準 LB 探測）和 POST（向後相容）
var brokerCrypto = app.Services.GetRequiredService<IEnvelopeCrypto>();
var healthHandler = () => Results.Ok(new
{
    status = "ok",
    timestamp = DateTime.UtcNow,
    broker_public_key = brokerCrypto.GetBrokerPublicKey()
});
api.MapGet("/health", healthHandler);
api.MapPost("/health", healthHandler);

// ── Dev RAG test（/dev/ 路徑已被 Encryption+Auth middleware 排除，無需額外保護） ──
{
    app.MapPost("/dev/rag-test", async (HttpContext ctx,
        BrokerCore.Data.BrokerDb ragTestDb,
        BrokerCore.Services.EmbeddingService ragTestEmbed) =>
    {
        var body = await System.Text.Json.JsonSerializer.DeserializeAsync<System.Text.Json.JsonElement>(ctx.Request.Body);
        var query = body.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
        var mode = body.TryGetProperty("mode", out var m) ? m.GetString() ?? "hybrid" : "hybrid";
        var limit = body.TryGetProperty("limit", out var lim) ? lim.GetInt32() : 5;

        if (string.IsNullOrEmpty(query))
            return Results.BadRequest(new { error = "query is required" });

        var taskId = "global";
        const int k = 60;

        // BM25
        var bm25 = new List<(string key, string content, double rank)>();
        try
        {
            var fts = ragTestDb.Query<RagFtsResult>(
                "SELECT source_key, content, rank FROM memory_fts WHERE memory_fts MATCH @q AND task_id = @taskId ORDER BY rank LIMIT @lim",
                new { q = Broker.Adapters.InProcessDispatcher.PrepareFts5Query(query), taskId, lim = limit * 3 });
            bm25 = fts.Select(r => (r.SourceKey ?? "", r.Content ?? "", r.Rank)).ToList();
        }
        catch (Exception ex) { return Results.Ok(new { error = $"FTS5 error: {ex.Message}" }); }

        // Vector
        var vec = new List<(string key, float sim)>();
        if (mode is "semantic" or "hybrid" && ragTestEmbed.IsEnabled)
        {
            var queryVec = await ragTestEmbed.EmbedAsync(query);
            if (queryVec != null)
            {
                var vectors = ragTestDb.GetAll<BrokerCore.Models.VectorEntry>()
                    .Where(v => v.TaskId == taskId && v.Embedding.Length > 0).ToList();
                vec = vectors.Select(v => {
                    var emb = BrokerCore.Services.EmbeddingService.BytesToVector(v.Embedding);
                    return (v.SourceKey, BrokerCore.Services.EmbeddingService.CosineSimilarity(queryVec, emb));
                }).Where(x => x.Item2 >= 0.2f).OrderByDescending(x => x.Item2).Take(limit * 3).ToList();
            }
        }

        // RRF
        var bm25s = new Dictionary<string, (float score, string content)>();
        int r = 1;
        foreach (var (bkey, bcontent, _) in bm25) { bm25s[bkey] = (1f / (k + r), bcontent); r++; }
        var vecs = new Dictionary<string, float>();
        r = 1;
        foreach (var (vkey, _) in vec) { vecs[vkey] = 1f / (k + r); r++; }

        var results = bm25s.Keys.Union(vecs.Keys).Distinct().Select(key => new
        {
            key,
            content = bm25s.TryGetValue(key, out var b) ? (b.content.Length > 500 ? b.content[..500] + "..." : b.content) :
                ragTestDb.GetAll<BrokerCore.Models.SharedContextEntry>()
                    .Where(e => e.TaskId == taskId && e.Key == key)
                    .OrderByDescending(e => e.Version)
                    .FirstOrDefault()?.ContentRef ?? "",
            rrf = MathF.Round((bm25s.TryGetValue(key, out var bs) ? bs.score : 0f) + vecs.GetValueOrDefault(key, 0f), 4),
            bm25_score = MathF.Round(bm25s.TryGetValue(key, out var bs2) ? bs2.score : 0f, 4),
            vec_score = MathF.Round(vecs.GetValueOrDefault(key, 0f), 4)
        }).OrderByDescending(x => x.rrf).Take(limit).ToList();

        return Results.Ok(new
        {
            query, mode, results, total = results.Count,
            stats = new {
                articles = ragTestDb.GetAll<BrokerCore.Models.SharedContextEntry>()
                    .Count(e => e.Key.StartsWith("消費者保護法:")),
                vectors = ragTestDb.GetAll<BrokerCore.Models.VectorEntry>()
                    .Count(v => v.SourceKey.StartsWith("消費者保護法:")),
                bm25_candidates = bm25.Count,
                vector_candidates = vec.Count
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
CapabilityEndpoints.Map(api);
ToolSpecEndpoints.Map(api);
BrowserBindingEndpoints.Map(api);
DeploymentAdminEndpoints.Map(api);
AuditEndpoints.Map(api);
ForensicsEndpoints.Map(api);
DataBrowserEndpoints.Map(api);
AdminEndpoints.Map(api);
ContextEndpoints.Map(api);
PlanEndpoints.Map(api);
RuntimeEndpoints.Map(api);
HighLevelEndpoints.Map(api);
GoogleDriveOAuthEndpoints.Map(api);
LocalAdminEndpoints.Map(api);
AgentEndpoints.Map(api);
// Agent Inbox（MVP-1 2026-05-01）— 任務派發給已 spawn 的 agent，獨立於 AgentEndpoints
AgentInboxEndpoints.Map(api);
// Agent Exec（MVP-2 2026-05-01）— agent 容器執行 capability_grants 裡授權的工具
AgentExecEndpoints.Map(api);
// LLM 代理觀測 endpoint（無論 pool 是否 enabled 都掛，因為這層跟 worker 無關）
LlmProxyEndpoints.Map(api);
// Artifact 下載 API(2026-05-29 AnthonyLee — Benson artifact-download plan 的新檔實作;平台功能、非 pool-gated)
ArtifactDownloadEndpoints.Map(api);

if (poolEnabled)
{
    WorkerEndpoints.Map(api);
    DiagnosticsEndpoints.Map(api);
    TelemetryEndpoints.Map(api);   // GET /telemetry/history — 唯讀健康時間序列(派發 telemetry.history)
    QuoteWorkerEndpoints.Map(api);
    QuoteOhlcvEndpoints.Map(api);
    TradingEndpoints.Map(api);
    StrategyEndpoints.Map(api);
    RiskEndpoints.Map(api);
    AutoTraderEndpoints.Map(api);
    ScannerEndpoints.Map(api);     // Phase 1 Step C — Scanner Hybrid 定義 + active legs view
    PortfolioSizingEndpoints.Map(api);  // Q1.6 — sizing 推薦(Kelly + ERC + vol-target + DD-aware)
    EmergencyEndpoints.Map(api);   // W14 P1+P5
    CapabilityGraphEndpoints.Map(api);   // G3 — capability registry graph
    PolicyReplayEndpoints.Map(api);      // G1 — sandbox PolicyEngine replay
    QuotaEndpoints.Map(api);             // H1 — per-principal LLM token / dispatch quota
    ApprovalTemplateEndpoints.Map(api);  // H3 — approval template CRUD
    TimeAclEndpoints.Map(api);           // H2 — time-windowed ACL CRUD
    AuditRagEndpoints.Map(api);          // H4 — audit search + LLM RAG summary
    MultiSigEndpoints.Map(api);          // I1 — multi-sig N-of-M approval rules
    DashboardEndpoints.Map(api);         // docker-stats / network-status / system-info
    AlertEndpoints.Map(api);
    AlertRulesEndpoints.Map(api);
    PerpetualEndpoints.Map(api);
    ScreenerEndpoints.Map(api);
    LabEndpoints.Map(api);
    AuthEndpoints.Map(api);
    ExchangeCredentialsEndpoints.Map(api);
    NotificationChannelsEndpoints.Map(api);   // 多用戶:每個 principal 管自己的推播頻道
    AdminUsersEndpoints.Map(api);
    ExportEndpoints.Map(api);
    HealthCheckEndpoints.Map(api);
    BacktestHistoryEndpoints.Map(api);
    PortfolioEndpoints.Map(api);
    NotificationEndpoints.Map(api);
    ResearchEndpoints.Map(api);
    KellyEndpoints.Map(api);
}
QuoteWebSocketEndpoints.Map(app);
AuditStreamEndpoints.Map(app);   // G2 — live audit event stream

// ── Phase 3: 啟動功能池 TCP Listener ──
if (poolEnabled)
{
    var poolListener = app.Services.GetRequiredService<PoolListener>();
    await poolListener.StartAsync();

    var healthMonitor = app.Services.GetRequiredService<WorkerHealthMonitor>();
    healthMonitor.Start();

    // ── 啟動排程診斷服務 ──
    var scheduledDiag = app.Services.GetRequiredService<ScheduledDiagnosticsService>();
    scheduledDiag.Start();

    // ── 啟動容器日誌採集（若註冊） ──
    var logTail = app.Services.GetService<ContainerLogTailService>();
    logTail?.Start();

    // 優雅關閉
    // L-8 修復：Register 只接受 Action（非 Func<Task>），sync-over-async 在 shutdown hook 不可避免
    // 加入 10 秒 timeout 防止 shutdown 卡住（無 SynchronizationContext，不會 deadlock）
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        // 1. 立刻設旗標：PoolDispatcher 之後拒絕新派發、不讓 in-flight 撞到 worker disconnect
        var shutdown = app.Services.GetService<FunctionPool.Dispatch.IShutdownState>();
        shutdown?.MarkStopping();

        // 2. 為每個還連著的 worker 寫一筆 audit、記下 broker 重啟事件（給之後 trace 對照）
        try
        {
            var registry = app.Services.GetService<FunctionPool.Registry.IWorkerRegistry>();
            var audit = app.Services.GetService<BrokerCore.Services.IAuditService>();
            if (registry != null && audit != null)
            {
                var traceId = $"trc_brk_shutdown_{DateTime.UtcNow:yyyyMMddHHmmss}";
                foreach (var w in registry.GetAllWorkers())
                {
                    audit.RecordEvent(traceId, "BROKER_SHUTDOWN",
                        principalId: "system",
                        resourceRef: w.WorkerId,
                        details: System.Text.Json.JsonSerializer.Serialize(new
                        {
                            worker_id = w.WorkerId,
                            capabilities = w.Capabilities,
                            active_tasks = w.ActiveTasks,
                            reason = "broker process stopping",
                        }));
                }
            }
        }
        catch { /* shutdown 路徑best-effort、別擋 */ }

        healthMonitor.Dispose();
        if (!poolListener.StopAsync().Wait(TimeSpan.FromSeconds(10)))
        {
            // StopAsync 超時，強制繼續 shutdown 流程
        }
    });
}

// ── RAG 種子（消費者保護法，僅首次執行） ──
{
    using var scope = app.Services.CreateScope();
    var ragDb = scope.ServiceProvider.GetRequiredService<BrokerCore.Data.BrokerDb>();
    var ragEmbed = scope.ServiceProvider.GetRequiredService<BrokerCore.Services.EmbeddingService>();
    var ragLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("RAGSeed");

    var existingArticles = ragDb.GetAll<BrokerCore.Models.SharedContextEntry>()
        .Where(e => e.Key.StartsWith("消費者保護法:")).ToList();
    var existingVectors = ragDb.GetAll<BrokerCore.Models.VectorEntry>()
        .Where(v => v.SourceKey.StartsWith("消費者保護法:")).Count();

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
                .Any(v => v.SourceKey == entry.Key && v.TaskId == (entry.TaskId ?? "global"));
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
                              value = Broker.Adapters.InProcessDispatcher.PrepareFts5Query(entry.ContentRef),
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
