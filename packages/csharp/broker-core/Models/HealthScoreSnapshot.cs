using BaseOrm;

namespace BrokerCore.Models;

/// <summary>
/// 平台整體健康分數的歷史 snapshot。
///
/// 由 HealthScoreSnapshotService 每 5 min 寫一筆。
/// 給 dashboard 顯示「過去 N 小時的健康趨勢」+ 報告附錄做時序圖用。
///
/// 沒做 per-worker 細節 snapshot（避免暴漲 row 數）：當下細節由 /health/score
/// 即時計算、歷史只記 overall + 三類計數。需要回看「某 worker 在 X 時間是 critical」
/// 可以用 audit_events 中該時段的 DISPATCH_FAILED 事件反推。
/// </summary>
[Table("health_score_snapshots")]
public class HealthScoreSnapshot
{
    // 2026-10:原本宣告成 string 並由服務產生 "hs_..."，但 [Key] 預設 AutoIncrement=true，
    // BaseOrm 建表時把這欄建成整數自增主鍵、寫入時也略過它，所產生的字串 ID 從未存進去。
    // 照實改成 long：建出來的表結構與既有部署完全相同，所以不需要遷移。
    [Key]
    [Column("snapshot_id")]
    public long SnapshotId { get; set; }

    [Column("captured_at")]
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    [Column("overall_score")]
    public int OverallScore { get; set; }

    [Column("overall_status")]
    [MaxLength(20)]
    public string OverallStatus { get; set; } = "";

    [Column("worker_count")]
    public int WorkerCount { get; set; }

    [Column("healthy_count")]
    public int HealthyCount { get; set; }

    [Column("degraded_count")]
    public int DegradedCount { get; set; }

    [Column("critical_count")]
    public int CriticalCount { get; set; }
}
