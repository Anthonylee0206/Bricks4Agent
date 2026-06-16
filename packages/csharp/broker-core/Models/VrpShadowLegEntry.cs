using BaseOrm;

namespace BrokerCore.Models;

/// <summary>
/// VRP / 波動 carry shadow 腿持久化(2026-06-16、見 docs/designs/vrp-shadow-deploy-sketch.md §2/§3.3)。
///
/// 為什麼獨立於 [[ScannerActiveLegEntry]]:scanner 腿是「方向性 long/short、PnL 來自價格反轉」;
/// VRP 是「delta 中性的變異數 carry、PnL = 隱含 − 實現變異數、跟標的漲跌無關」。硬塞方向性 leg 會測錯,
/// 故走獨立記錄路徑(sketch §2 的核心決策)。本表只存「量測輸入(隱含 DVOL/spot/sizing)+ 結算輸出
/// (實現波動 + 裸賣/封頂雙 PnL)」,condor 定價/strike 選擇/sizing 公式屬 alpha、留私有 strategy-worker。
///
/// 一條 leg = 一個 roll 視窗:EntryTime 開、RollEndTime 結算。SettledAt IS NULL = 還在持倉。
/// 同時記 PnlPctNaked(裸賣變異數、研究的「不可部署幻覺」基準)與 PnlPctCapped(defined-risk 封頂、
/// 可部署值)→ shadow 自證封頂有效。冪等鎖:Id = "vrp:{currency}:{entry_ms}" 同視窗不重開。
/// </summary>
[Table("vrp_shadow_legs")]
public class VrpShadowLegEntry
{
    [Key(AutoIncrement = false)]
    [Column("id")]
    [MaxLength(80)]
    public string Id { get; set; } = string.Empty;

    /// <summary>Deribit 標的幣別("BTC" / "ETH";strategy 側目前 BTC only)。</summary>
    [Column("currency")]
    [Required]
    [MaxLength(16)]
    public string Currency { get; set; } = "BTC";

    /// <summary>進場時間(UTC);roll 視窗起點。冪等鎖第二部分(轉 unix ms 進 Id)。</summary>
    [Column("entry_time")]
    public DateTime EntryTime { get; set; } = DateTime.UtcNow;

    /// <summary>結算時間(UTC)= EntryTime + HorizonDays;到期才算 realized vol + PnL。</summary>
    [Column("roll_end_time")]
    public DateTime RollEndTime { get; set; }

    /// <summary>roll 視窗天數(e.g. 30)。</summary>
    [Column("horizon_days")]
    public int HorizonDays { get; set; } = 30;

    /// <summary>進場時隱含波動(Deribit DVOL、年化 %)= 賣方收的 implied。</summary>
    [Column("dvol_entry")]
    public decimal DvolEntry { get; set; }

    /// <summary>進場時標的 spot(對照、left-tail 估算用)。</summary>
    [Column("spot_entry")]
    public decimal SpotEntry { get; set; }

    /// <summary>sizing 倍率(∝DVOL、cap 1.0;只降不槓、過 sizing-overlay 槓桿檢驗)。alpha 公式在 recorder 內、本欄只存結果。</summary>
    [Column("size_scalar")]
    public decimal SizeScalar { get; set; } = 1m;

    /// <summary>sleeve 名目 USDT(小權重)。</summary>
    [Column("notional")]
    public decimal Notional { get; set; }

    /// <summary>記錄的結構:"naked_variance"(裸賣基準)/ "iron_condor"(defined-risk)。</summary>
    [Column("structure")]
    [MaxLength(24)]
    public string Structure { get; set; } = "iron_condor";

    /// <summary>defined-risk 封頂百分比(e.g. 50 = 單視窗虧損封到 −50%);PnlPctCapped 用。</summary>
    [Column("cap_pct")]
    public decimal CapPct { get; set; } = 50m;

    /// <summary>shadow 開倉(只記不下單)。對齊 [[ScannerActiveLegEntry]].Shadow;VRP 真錢執行 adapter 是 Phase 3。</summary>
    [Column("shadow")]
    public bool Shadow { get; set; } = true;

    // ── 結算狀態(到 RollEndTime 才填)──

    /// <summary>結算時間(UTC);NULL = roll 視窗未到、還在持倉。</summary>
    [Column("settled_at")]
    public DateTime? SettledAt { get; set; }

    /// <summary>視窗內實現波動(年化 %、從標的 OHLCV 日報酬算)= 賣方要賠的 realized。</summary>
    [Column("realized_vol")]
    public decimal RealizedVol { get; set; }

    /// <summary>裸賣變異數 PnL %(∝ implied² − realized²;研究的「不可部署幻覺」基準、左尾無封頂)。</summary>
    [Column("pnl_pct_naked")]
    public decimal PnlPctNaked { get; set; }

    /// <summary>defined-risk(condor)封頂後 PnL %(虧損封到 −CapPct;可部署值)。</summary>
    [Column("pnl_pct_capped")]
    public decimal PnlPctCapped { get; set; }

    /// <summary>結算原因:"settled"(到期正常結算)/ "manual" / "expired"。</summary>
    [Column("close_reason")]
    [MaxLength(20)]
    public string CloseReason { get; set; } = string.Empty;

    [Column("owner_principal_id")]
    [MaxLength(80)]
    public string OwnerPrincipalId { get; set; } = "prn_dashboard";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
