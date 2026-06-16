using System.Net.Http.Json;
using System.Text.Json;
using BrokerCore.Contracts;
using BrokerCore.Services;
using FunctionPool.Registry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Broker.Services;

/// <summary>
/// 治理層主動告警——把「平台狀態變糟」這類事件主動推到 Discord / LINE，
/// admin 不必盯 dashboard 也能在第一時間知道。
///
/// 不重複既有 DiscordNotificationService / LineNotificationService 的功能：
///   - 那兩個服務 watch PriceAlertService.History + AutoTraderService.RecentLogs
///     （價格告警 + 交易動作），是 trading-domain 事件
///   - 本服務 watch HealthScoreService + IApprovalService（governance 事件）
///
/// 觸發點（每 60s tick 一次）：
///   1. Health overall_status transition：healthy → degraded 或 → critical（變壞才推）
///   2. 新 pending approval（之前沒看過的 approval_id）
///
/// 推送方式：
///   - Discord：直接 POST webhook（config Notifications:Discord:WebhookUrl）
///   - LINE：dispatch line.notification.send capability（同 LineNotificationService 路徑）
///   - 兩端同時推、只要其一可用就推；都沒設則 service 純跑空 tick
///
/// 去重：用 in-memory hashset 追已推送過的 approval_id；status transition 用「上次看到的狀態」
/// 比對，連續同狀態不重推（避免每 60s spam）。
/// </summary>
public class GovernanceAlertsService : BackgroundService
{
    private readonly HealthScoreService _healthSvc;
    private readonly ITradingApprovalService _approval;
    private readonly IExecutionDispatcher _dispatcher;
    private readonly IWorkerRegistry _registry;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<GovernanceAlertsService> _logger;

    private readonly string _discordWebhook;
    private readonly bool _lineEnabled;
    private readonly string? _lineRecipient;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private string? _lastSeenStatus;             // healthy / degraded / critical
    private readonly HashSet<string> _seenPendingIds = new();
    // 啟動時吸收既有 pending、不要把第一輪歷史全部當「新事件」推爆
    private bool _initialized = false;

    public GovernanceAlertsService(
        HealthScoreService healthSvc,
        ITradingApprovalService approval,
        IExecutionDispatcher dispatcher,
        IWorkerRegistry registry,
        IHttpClientFactory httpFactory,
        IConfiguration config,
        ILogger<GovernanceAlertsService> logger)
    {
        _healthSvc = healthSvc;
        _approval = approval;
        _dispatcher = dispatcher;
        _registry = registry;
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;

        _discordWebhook = config["Notifications:Discord:WebhookUrl"] ?? "";
        _lineEnabled = config.GetValue("Notifications:Line:Enabled", false);
        _lineRecipient = config["Notifications:Line:RecipientId"];
    }

    public bool DiscordEnabled => !string.IsNullOrWhiteSpace(_discordWebhook);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "GovernanceAlerts started, interval={S}s, discord={D}, line={L}",
            TickInterval.TotalSeconds, DiscordEnabled, _lineEnabled);

        // 等 broker 整個起來 + worker 連上、避免 cold-start 假警報
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Governance alerts tick failed"); }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // 1. Health Score：偵測狀態惡化
        var report = await _healthSvc.ComputeAsync(ct);
        if (report.WorkerCount > 0)
        {
            var current = report.OverallStatus;
            if (_initialized && _lastSeenStatus != null && current != _lastSeenStatus)
            {
                // 只在「變壞」時推（degraded > healthy 時才提醒；恢復就不打擾）
                var rank = StatusRank(current);
                var prevRank = StatusRank(_lastSeenStatus);
                if (rank > prevRank)
                {
                    var sub = $"{report.HealthyCount} healthy · {report.DegradedCount} degraded · {report.CriticalCount} critical";
                    var worstWorker = report.Workers
                        .OrderByDescending(w => StatusRank(w.Status))
                        .FirstOrDefault();
                    var worstHint = worstWorker == null
                        ? ""
                        : $"\n問題 worker：`{worstWorker.WorkerId}` (score={worstWorker.Score})";
                    await PushAlertAsync(
                        title: $"⚠ Platform health → {current}",
                        body: $"Overall {report.OverallScore}/100 · {sub}{worstHint}",
                        severity: rank >= 2 ? "critical" : "warn",
                        color: rank >= 2 ? 0xE74C3C : 0xE6A23C,
                        ct: ct);
                }
            }
            _lastSeenStatus = current;
        }

        // 2. Pending approvals：偵測新請求
        var pending = _approval.List(status: "pending", limit: 50);
        if (!_initialized)
        {
            // 第一輪：吸收既有 pending、不重複推
            foreach (var p in pending) _seenPendingIds.Add(p.ApprovalId);
        }
        else
        {
            foreach (var p in pending)
            {
                if (_seenPendingIds.Contains(p.ApprovalId)) continue;
                _seenPendingIds.Add(p.ApprovalId);
                // W14 P3：把 risk hint 一起推、admin 一眼看出風險量級
                var riskHint = ApprovalRiskHintHelper.Hint(p.CapabilityId, p.Payload);
                await PushAlertAsync(
                    title: $"🔐 Pending approval: {p.CapabilityId}",
                    body: $"`{p.PrincipalId}` ({p.Role}) requested `{p.CapabilityId}` route=`{p.Route}`\n{riskHint}\napproval_id=`{p.ApprovalId}` · 到 dashboard /待審 分頁裁決",
                    severity: "warn",
                    color: 0xE6A23C,
                    ct: ct,
                    // Discord 推播跳過——bot-node 自己會在 Discord 頻道發按鈕訊息、走互動式審核
                    skipDiscord: true);
            }
        }

        _initialized = true;
    }

    private static int StatusRank(string status) => status switch
    {
        "healthy"  => 0,
        "degraded" => 1,
        "critical" => 2,
        _          => 0,
    };

    /// <summary>同時推 Discord webhook + LINE capability dispatch，best-effort。</summary>
    /// <param name="skipDiscord">true → 跳過 Discord 推播。Approval 類用 true、因為 bot-node
    /// 自己在 Discord 頻道發互動式按鈕訊息、broker 不必再推一次純文字訊息（重複通知）。</param>
    private async Task PushAlertAsync(
        string title, string body, string severity, int color, CancellationToken ct,
        bool skipDiscord = false)
    {
        _logger.LogInformation("Governance alert: [{S}] {T}", severity, title);

        // Discord
        if (DiscordEnabled && !skipDiscord)
        {
            try
            {
                using var client = _httpFactory.CreateClient("governance-alerts");
                client.Timeout = TimeSpan.FromSeconds(10);
                var payload = new
                {
                    username = "B4A Broker",
                    embeds = new[]
                    {
                        new
                        {
                            title,
                            description = body,
                            color,
                            timestamp = DateTime.UtcNow.ToString("o"),
                            footer = new { text = "B4A Governance Alerts" },
                        },
                    },
                };
                var resp = await client.PostAsJsonAsync(_discordWebhook, payload, ct);
                if (!resp.IsSuccessStatusCode)
                    _logger.LogDebug("Discord alert returned {Code}", resp.StatusCode);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Discord alert push failed"); }
        }

        // LINE — via line.notification.send capability
        if (_lineEnabled && _registry.HasAvailableWorker("line.notification.send"))
        {
            try
            {
                var args = new Dictionary<string, object?>
                {
                    ["title"] = title,
                    ["body"]  = body,
                    ["level"] = severity == "critical" ? "error" : (severity == "warn" ? "warn" : "info"),
                };
                if (!string.IsNullOrEmpty(_lineRecipient)) args["to"] = _lineRecipient;
                var payload = JsonSerializer.Serialize(new { args });
                var req = new ApprovedRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    CapabilityId = "line.notification.send",
                    Route = "send",
                    Payload = payload,
                    Scope = "{}", PrincipalId = "system",
                    TaskId = "governance-alerts", SessionId = "governance-alerts",
                };
                var result = await _dispatcher.DispatchAsync(req);
                if (!result.Success)
                    _logger.LogDebug("LINE alert dispatch failed: {E}", result.ErrorMessage);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "LINE alert push failed"); }
        }
    }
}
