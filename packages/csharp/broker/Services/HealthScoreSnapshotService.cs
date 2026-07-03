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

    // 磁碟空間守衛:同 tick 週期順檢(免額外 loop);連續 2 tick 低水位才告警(濾暫態)。
    // 動機:韌性審計指出「重啟失敗 + 人在睡」情境下磁碟寫滿是無聲殺手(DB/log 寫入失敗 → 平台靜默退化)。
    private const int DiskAlertThreshold = 2;
    private int _consecutiveDiskLow;
    private bool _diskAlertActive;

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

        // 磁碟空間守衛(同 tick 順檢、同一套 edge-triggered 告警模式)
        EvaluateDiskAlert();

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
        var (next, shouldFire) = NextCriticalAlertState(
            new CriticalAlertState(_consecutiveCritical, _alertActive), critical, CriticalAlertThreshold);

        if (!critical && _alertActive)
            _logger.LogInformation("Health recovered (overall={Score}), clearing critical alert", report.OverallScore);

        _consecutiveCritical = next.ConsecutiveCritical;
        _alertActive = next.AlertActive;
        if (!shouldFire) return;

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

    /// <summary>連續 critical 告警的純決策狀態(供確定性測試)。</summary>
    public readonly record struct CriticalAlertState(int ConsecutiveCritical, bool AlertActive);

    /// <summary>
    /// 純函數:給定前一狀態 + 本 tick 是否 critical + 門檻,算出新狀態與是否該觸發告警。
    /// edge-triggered:達門檻當下觸發一次,持續期間不重複;非 critical 即 reset 計數與旗標。
    /// </summary>
    public static (CriticalAlertState next, bool shouldFire) NextCriticalAlertState(
        CriticalAlertState prev, bool critical, int threshold)
    {
        if (!critical)
            return (new CriticalAlertState(0, false), false);

        var consecutive = prev.ConsecutiveCritical + 1;
        var shouldFire = consecutive >= threshold && !prev.AlertActive;
        return (new CriticalAlertState(consecutive, prev.AlertActive || shouldFire), shouldFire);
    }

    /// <summary>查歷史 snapshot（給 endpoint 用）。</summary>
    public List<HealthScoreSnapshot> GetHistory(int sinceMinutes = 360)
    {
        var since = DateTime.UtcNow.AddMinutes(-sinceMinutes);
        return _db.Query<HealthScoreSnapshot>(
            "SELECT * FROM health_score_snapshots WHERE captured_at > @since ORDER BY captured_at ASC",
            new { since });
    }

    // ── 磁碟空間守衛 ─────────────────────────────────────────────────

    /// <summary>磁碟水位等級。</summary>
    public enum DiskLevel { Ok, Warning, Critical }

    /// <summary>
    /// 純函數:磁碟水位判定。百分比與絕對值取「較嚴重者」——大磁碟看百分比會太晚叫、
    /// 小磁碟看絕對值會誤叫,兩者併用:
    ///   Critical:free &lt; 7% 或 free &lt; 2GB;Warning:free &lt; 15% 或 free &lt; 5GB。
    /// total&lt;=0(抓不到磁碟資訊)→ Ok(缺資料不懲罰、與健康分數同哲學)。
    /// </summary>
    public static DiskLevel EvaluateDiskLevel(long totalBytes, long freeBytes)
    {
        if (totalBytes <= 0) return DiskLevel.Ok;
        const long GB = 1024L * 1024 * 1024;
        var freePct = (double)freeBytes / totalBytes;
        if (freePct < 0.07 || freeBytes < 2 * GB) return DiskLevel.Critical;
        if (freePct < 0.15 || freeBytes < 5 * GB) return DiskLevel.Warning;
        return DiskLevel.Ok;
    }

    /// <summary>
    /// 檢查 broker 相依的磁碟(base dir 所在磁碟 + /data volume 若存在,容器內兩者常是不同檔案系統),
    /// 任一達 Warning 以上、且連續 DiskAlertThreshold 個 tick → 記 DISK_SPACE_LOW 觀測告警
    /// (同 EvaluateCriticalAlert 的 edge-triggered 模式:觸發一次、恢復才 reset)。
    /// </summary>
    private void EvaluateDiskAlert()
    {
        var findings = new List<(string Path, long Total, long Free, DiskLevel Level)>();
        foreach (var path in EnumerateWatchedDiskPaths())
        {
            try
            {
                var di = new DriveInfo(Path.GetFullPath(path));
                var level = EvaluateDiskLevel(di.TotalSize, di.AvailableFreeSpace);
                findings.Add((path, di.TotalSize, di.AvailableFreeSpace, level));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Disk check failed for {Path} (skipped)", path);
            }
        }
        if (findings.Count == 0) return;

        var worst = findings.Max(f => f.Level);
        var low = worst != DiskLevel.Ok;
        var (next, shouldFire) = NextCriticalAlertState(
            new CriticalAlertState(_consecutiveDiskLow, _diskAlertActive), low, DiskAlertThreshold);

        if (!low && _diskAlertActive)
            _logger.LogInformation("Disk space recovered, clearing DISK_SPACE_LOW alert");

        _consecutiveDiskLow = next.ConsecutiveCritical;
        _diskAlertActive = next.AlertActive;
        if (!shouldFire) return;

        try
        {
            _observations.Record(new ObservationEvent
            {
                TraceId   = BrokerCore.IdGen.New("htrace"),
                EventType = "HEALTH_DISK_SPACE_LOW",
                Source    = ObservationSource.Internal,
                Severity  = worst == DiskLevel.Critical ? ObservationSeverity.Critical : ObservationSeverity.Alert,
                ObservedState = JsonSerializer.Serialize(new
                {
                    disks = findings.Select(f => new
                    {
                        path = f.Path, level = f.Level.ToString(),
                        total_gb = Math.Round(f.Total / 1073741824.0, 1),
                        free_gb  = Math.Round(f.Free / 1073741824.0, 1),
                    }),
                }),
                Details = JsonSerializer.Serialize(new
                {
                    sustainedTicks = _consecutiveDiskLow,
                    thresholdTicks = DiskAlertThreshold,
                    note = "Disk free space below watermark sustained across snapshots (DB/log writes at risk)",
                }),
            });
            _logger.LogWarning("Disk space low sustained {Ticks} ticks ({Worst}) — recorded HEALTH_DISK_SPACE_LOW observation",
                _consecutiveDiskLow, worst);
        }
        catch (Exception ex)
        {
            _diskAlertActive = false;   // 同 critical alert:記錄失敗放掉旗標、下 tick 再試
            _logger.LogWarning(ex, "Failed to record HEALTH_DISK_SPACE_LOW observation");
        }
    }

    /// <summary>要盯的磁碟路徑:base dir 為主;容器裡的 /data volume(DB 所在)常是另一個檔案系統、存在就一起盯。</summary>
    private static IEnumerable<string> EnumerateWatchedDiskPaths()
    {
        yield return AppContext.BaseDirectory;
        if (Directory.Exists("/data")) yield return "/data";
    }

    // ── Snapshot 管線死人開關(dead-man)───────────────────────────────

    /// <summary>
    /// 純函數:snapshot 管線是否停擺。讀取端(endpoint)呼叫 —— 寫入 loop 若死掉無法自我回報,
    /// 只有「由外部消費者在讀取時計算」才構成真正的 dead-man。
    /// lastCapturedAt=null(從未有 snapshot)→ 不算停擺(避免冷啟動假警報),回 (false, null)。
    /// 超過 staleFactor × tickInterval 沒新 snapshot → 停擺。
    /// </summary>
    public static (bool Stalled, double? AgeSeconds) ComputeSnapshotStaleness(
        DateTime? lastCapturedAt, DateTime nowUtc, TimeSpan tickInterval, int staleFactor = 3)
    {
        if (lastCapturedAt == null) return (false, null);
        var age = (nowUtc - lastCapturedAt.Value).TotalSeconds;
        if (age < 0) age = 0;   // 時鐘漂移防呆
        return (age > staleFactor * tickInterval.TotalSeconds, age);
    }

    /// <summary>tick 間隔(dead-man 計算用、endpoint 取用)。</summary>
    public static TimeSpan SnapshotTickInterval => TickInterval;

    /// <summary>最新一張 snapshot 的時間(dead-man 用);無資料回 null。</summary>
    public DateTime? GetLatestSnapshotTime()
    {
        try
        {
            var row = _db.QueryFirst<HealthScoreSnapshot>(
                "SELECT * FROM health_score_snapshots ORDER BY captured_at DESC LIMIT 1");
            return row?.CapturedAt;
        }
        catch { return null; }
    }
}
