using BrokerCore.Data;
using BrokerCore.Models;
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
    private readonly ILogger<HealthScoreSnapshotService> _logger;

    /// <summary>快照間隔。異常偵測報告用它判斷快照是否中斷（R12）。</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public HealthScoreSnapshotService(
        HealthScoreService scoreSvc,
        BrokerDb db,
        LeaderGuard guard,
        ILogger<HealthScoreSnapshotService> logger)
    {
        _scoreSvc = scoreSvc;
        _db = db;
        _guard = guard;
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
        // 沒有 worker 時也照記（status=no_workers、score=0）。原本這裡直接 return，
        // 結果 worker 全掛的那段時間在歷史上是空白，看起來像「沒資料」而不是「出事」。

        _db.Insert(new HealthScoreSnapshot
        {
            CapturedAt    = report.GeneratedAt,
            OverallScore  = report.OverallScore,
            OverallStatus = report.OverallStatus,
            WorkerCount   = report.WorkerCount,
            HealthyCount  = report.HealthyCount,
            DegradedCount = report.DegradedCount,
            CriticalCount = report.CriticalCount,
        });

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

    /// <summary>查歷史 snapshot（給 endpoint 用）。</summary>
    public List<HealthScoreSnapshot> GetHistory(int sinceMinutes = 360)
    {
        var since = DateTime.UtcNow.AddMinutes(-sinceMinutes);
        return _db.Query<HealthScoreSnapshot>(
            "SELECT * FROM health_score_snapshots WHERE captured_at > @since ORDER BY captured_at ASC",
            new { since });
    }
}
