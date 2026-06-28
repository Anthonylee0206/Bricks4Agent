using System.Text.Json;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Broker.Services;

/// <summary>
/// 每 5 min 拍一張平台整體健康分數的 snapshot 進 health_score_snapshots 表。
///
/// 給 dashboard 顯示「過去 N 小時的健康趨勢」+ 報告做時序圖。
/// 滾動清理：超過 7 天的 snapshot 自動刪（每次 tick 順便做）。
///
/// 一次 tick 寫一行（24h × 12 snapshot/h = 288 行/天 × 7 天 = ~2000 行 ceiling）、輕。
/// </summary>
public class HealthScoreSnapshotService : BackgroundService
{
    private readonly HealthScoreService _scoreSvc;
    private readonly BrokerDb _db;
    private readonly LeaderGuard _guard;
    private readonly IObservationService _observations;
    private readonly ILogger<HealthScoreSnapshotService> _logger;

    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    // 連續 N 個 tick 都 critical 才告警（5min × 3 = 持續 ~15min），濾掉瞬間抖動 / 重啟暫態。
    private const int CriticalAlertThreshold = 3;
    private int _consecutiveCritical;
    private bool _alertActive;

    public HealthScoreSnapshotService(
        HealthScoreService scoreSvc,
        BrokerDb db,
        LeaderGuard guard,
        IObservationService observations,
        ILogger<HealthScoreSnapshotService> logger)
    {
        _scoreSvc = scoreSvc;
        _db = db;
        _guard = guard;
        _observations = observations;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "HealthScoreSnapshot started, interval={Min}min, retention={Days}d",
            TickInterval.TotalMinutes, Retention.TotalDays);

        // 等 broker 起來、worker 連上、health 分數有意義
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "Health snapshot tick failed"); }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // 階段②:只有 PRIMARY(或單機)寫 snapshot;多節點時 STANDBY 自我跳過、避免雙寫
        if (!_guard.ShouldRun("health-snapshot")) return;
        var report = await _scoreSvc.ComputeAsync(ct);
        if (report.WorkerCount == 0) return;  // 沒 worker 連上、別記 noise

        _db.Insert(new HealthScoreSnapshot
        {
            SnapshotId    = BrokerCore.IdGen.New("hs"),
            CapturedAt    = report.GeneratedAt,
            OverallScore  = report.OverallScore,
            OverallStatus = report.OverallStatus,
            WorkerCount   = report.WorkerCount,
            HealthyCount  = report.HealthyCount,
            DegradedCount = report.DegradedCount,
            CriticalCount = report.CriticalCount,
        });

        // 持續 critical → 記一條治理級觀測告警（進 audit hash-chain、可 dashboard / 外部 watchdog 撈）
        EvaluateCriticalAlert(report);

        // 滾動清理：刪掉超過 retention 的舊 snapshot
        var cutoff = DateTime.UtcNow - Retention;
        try
        {
            _db.Execute(
                "DELETE FROM health_score_snapshots WHERE captured_at < @cutoff",
                new { cutoff });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Health snapshot retention cleanup failed (non-fatal)");
        }
    }

    /// <summary>
    /// 連續 N 個 snapshot 都 critical 才升一次告警（edge-triggered，恢復才 reset），
    /// 走 ObservationService → 自動進 audit hash-chain，不外連、不碰密鑰。
    /// 原本只有 snapshot 時序、無「持續惡化」的主動告警鏈，這裡補上。
    /// </summary>
    private void EvaluateCriticalAlert(HealthScoreReport report)
    {
        var critical = report.OverallStatus == "critical" || report.CriticalCount > 0;
        if (!critical)
        {
            if (_alertActive)
                _logger.LogInformation("Health recovered (overall={Score}), clearing critical alert", report.OverallScore);
            _consecutiveCritical = 0;
            _alertActive = false;
            return;
        }

        _consecutiveCritical++;
        if (_consecutiveCritical < CriticalAlertThreshold || _alertActive) return;
        _alertActive = true;  // edge-trigger：持續期間只記一次，避免每 5min 灌 noise

        try
        {
            var criticalWorkers = report.Workers
                .Where(w => w.Status == "critical")
                .Select(w => w.WorkerId)
                .ToList();

            _observations.Record(new ObservationEvent
            {
                TraceId   = BrokerCore.IdGen.New("htrace"),
                EventType = "HEALTH_SCORE_CRITICAL",
                Source    = ObservationSource.Internal,
                Severity  = ObservationSeverity.Critical,
                ObservedState = JsonSerializer.Serialize(new
                {
                    overallScore  = report.OverallScore,
                    overallStatus = report.OverallStatus,
                    workerCount   = report.WorkerCount,
                    criticalCount = report.CriticalCount,
                    degradedCount = report.DegradedCount,
                }),
                Details = JsonSerializer.Serialize(new
                {
                    sustainedTicks = _consecutiveCritical,
                    thresholdTicks = CriticalAlertThreshold,
                    criticalWorkers,
                    note = "Overall worker health critical sustained across snapshots",
                }),
            });

            _logger.LogWarning(
                "Health critical sustained {Ticks} ticks (overall={Score}, criticalWorkers={Count}) — recorded HEALTH_SCORE_CRITICAL observation",
                _consecutiveCritical, report.OverallScore, criticalWorkers.Count);
        }
        catch (Exception ex)
        {
            // 告警失敗不可拖垮 snapshot 主流程；放掉 _alertActive 讓下個 tick 再試
            _alertActive = false;
            _logger.LogWarning(ex, "Failed to record HEALTH_SCORE_CRITICAL observation");
        }
    }

    /// <summary>查歷史 snapshot（給 endpoint 用）。</summary>
    public List<HealthScoreSnapshot> GetHistory(int sinceMinutes = 360)
    {
        var since = DateTime.UtcNow.AddMinutes(-sinceMinutes);
        return _db.Query<HealthScoreSnapshot>(
            "SELECT * FROM health_score_snapshots WHERE captured_at > @since ORDER BY captured_at ASC",
            new { since });
    }
}
