using System.Collections.Concurrent;
using System.Text.Json;
using BrokerCore.Contracts;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;
using FunctionPool.Registry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Broker.Services;

/// <summary>
/// 自動交易背景服務。
///
/// 流程：拉 K 線 → 策略分析 → 風控檢查 → 下單
/// 可透過 API 動態新增/移除/暫停監控的 symbol。
///
/// Watchlist 持久化到 SQLite（auto_trade_watchlist 表，2026-05-02 補完）。
/// 啟動時 load 所有 entry 重建記憶體 dict；任何變更（add/remove/pause/resume/qty 調整）
/// 同步寫回 DB——這樣 broker 重啟後監控清單不會消失。
/// </summary>
public class AutoTraderService : BackgroundService
{
    private readonly IExecutionDispatcher _dispatcher;
    private readonly IWorkerRegistry _registry;
    private readonly BrokerDb _db;
    private readonly ILogger<AutoTraderService> _logger;

    private readonly ConcurrentDictionary<string, WatchItem> _watchList = new();
    private readonly ConcurrentQueue<TradeLog> _tradeLog = new();
    private const int MaxLogEntries = 200;

    private int _intervalSeconds = 300; // 預設 5 分鐘
    private bool _enabled = false;

    /// <summary>上次 cycle 開始時間（UTC）。null = 從未跑過 / 沒 enabled。
    /// 給 /metrics 跟 heartbeat watchdog 看「auto-trader 是不是真的有在跑」用。</summary>
    private DateTime? _lastCycleAt;
    public DateTime? LastCycleAt => _lastCycleAt;

    /// <summary>
    /// 開發/測試用：env AUTOTRADER_DEV_FORCE_ACTION=buy|sell 會強制覆蓋 strategy 訊號
    /// （繞過 action=="hold" early-return 跟 confidence threshold），讓 e2e 真的打到
    /// 交易所。**只該在 paper 帳號用、用完一定要 unset**——每一輪會印 WARNING log 提醒。
    /// 預設 null = 不啟用、走原本訊號驅動邏輯。
    /// </summary>
    private readonly string? _devForceAction;

    /// <summary>
    /// 信心度門檻——env AUTOTRADER_MIN_CONFIDENCE 可覆蓋（合法範圍 [0, 1]）。
    /// 預設 0.5。Composite 策略 + 三道過濾鏈（hold 稀釋 + auto-trader 門檻 + risk
    /// dampening）很容易把 buy/sell 訊號擋下來，這個 knob 讓 paper 階段可以放寬到 0.45-0.5
    /// 觀察出單頻率，正式上線再拉回 0.6+。
    /// </summary>
    private readonly decimal _minConfidence;

    /// <summary>
    /// Portfolio 當日最大 drawdown 上限（%）。env AUTOTRADER_MAX_PORTFOLIO_DD_PCT 可覆蓋。
    /// 預設 8。每個 exchange 各自追蹤當日 peak，當 (peak - current) / peak ≥ 此 % 時，
    /// 該 exchange 的所有新單會被該 cycle 擋下（既有持倉不動）；peak 在 UTC 午夜重置。
    /// 跟 risk-worker 的 max_drawdown_pct 規則互補：那條看「歷史總高」、這條看「當日最高」。
    /// </summary>
    private readonly decimal _maxPortfolioDdPct;
    private readonly ConcurrentDictionary<string, PortfolioPeakState> _peakByExchange = new();

    public string? DevForceAction => _devForceAction;
    public decimal MinConfidence => _minConfidence;
    public decimal MaxPortfolioDdPct => _maxPortfolioDdPct;
    public IReadOnlyDictionary<string, object> CircuitBreakerSnapshot =>
        _peakByExchange.ToDictionary(
            kv => kv.Key,
            kv => (object)new
            {
                peak_value     = kv.Value.PeakValue,
                last_value     = kv.Value.LastValue,
                dd_pct         = kv.Value.LastDdPct,
                triggered      = kv.Value.LastTriggered,
                threshold_pct  = _maxPortfolioDdPct,
                peak_reset_at  = kv.Value.PeakResetAt,
                last_update    = kv.Value.LastUpdate,
            });

    /// <summary>每 exchange 一份的當日 peak / 最近一次評估快照。</summary>
    internal class PortfolioPeakState
    {
        public decimal PeakValue;
        public decimal LastValue;
        public decimal LastDdPct;
        public bool LastTriggered;
        public DateTime PeakResetAt;  // UTC 當日 00:00
        public DateTime LastUpdate;
    }

    /// <summary>單次 circuit breaker 評估結果。</summary>
    public class CircuitBreakerEval
    {
        public bool Triggered    { get; init; }
        public decimal PeakValue { get; init; }
        public decimal CurrentValue { get; init; }
        public decimal DdPct     { get; init; }
        public decimal Threshold { get; init; }
        public DateTime PeakResetAt { get; init; }
    }

    // ── B4a Position protection ─────────────────────────────────────
    //
    // 每個 open position 一份 state，每 cycle 跑 sweep：
    //   1. SL hit  → 賣全部 → 清 state（下次 sweep 由 qty=0 觸發 cleanup）
    //   2. 漲 ≥ partial_exit_pct → 賣 partial_exit_ratio 的量、partial_exited=true（只觸發 1 次）
    //   3. 漲 ≥ breakeven_trigger_pct → SL 移到 entry × (1 + buffer) (be_moved=true)
    //
    // SL 是 broker 端「軟 SL」——不打交易所的 stop-loss order，每 cycle 拉 current_price
    // 比對 sl_price 自己決定要不要平倉。好處：可動態調整、不用管 exchange 的 SL 規格差異。
    // 風險：cycle 間隔 5 分鐘期間若有 flash crash，會錯過。配 B1 circuit breaker 為兜底。
    // D1 Phase 2 — ProtectionConfig / PositionProtectionState / PerpetualPositionState /
    // ProtectionAction / ProtectionDecision / PerpProtectionAction / PerpProtectionDecision /
    // SlHitRecord 已抽到 ProtectionDecisionEngine.cs（同 namespace、引用不變）。

    private readonly ProtectionConfig _protectionConfig;
    private readonly ConcurrentDictionary<string, PositionProtectionState> _positionState = new();

    public ProtectionConfig PositionProtectionConfig => _protectionConfig;
    public IReadOnlyDictionary<string, PositionProtectionState> PositionStates => _positionState;

    // ── Phase 4: Perpetual position protection — PerpetualPositionState 移到
    //    ProtectionDecisionEngine.cs（同 namespace、引用不變）
    private readonly ConcurrentDictionary<string, PerpetualPositionState> _perpPositionState = new();
    // 2026-06-04:橋接「開倉路徑解析到的 signal StopPrice」→「保護 scan 的 SL init」。
    // key = {owner}:{exchange}:{symbol}:{side}(同 perp protection state key)。只在 UseSignalSl 腿填、平倉時清。
    private readonly ConcurrentDictionary<string, decimal> _signalSlByKey = new();
    // finding J:hedge 模式同方向倉合併成 1 筆 position row、無法用筆數當加碼深度 → 改用 broker 本地累計加碼次數。
    // key = {owner}:{exchange}:{symbol}:{side}(同 perp protection state key);平倉於 stale-key 清除迴圈一併清掉。
    private readonly ConcurrentDictionary<string, int> _scaleInCountByKey = new();
    private readonly decimal _perpLiqEmergencyPct;
    private readonly decimal _dynamicRiskPct;   // 開倉時 max_loss 佔帳戶比例（預設 2%、對齊 r14）
    private readonly decimal _maxPortfolioRiskPct;  // 所有開倉 combined max_loss 上限（預設 6%、對齊 r16）
    // ── 全倉（cross margin）曝險比例 sizing — opt-in、預設 0 = 關（用上面的 risk/SL sizing）。
    //    notional = exposurePct% × 帳戶總資金;判斷風險用「總曝險 / 總資金」而非 per-trade SL。
    //    配合 cross margin：無 per-position 強平、可接受單倉 > 100% 虧損、整帳戶為後盾。
    private readonly decimal _exposurePct;            // 每倉 notional 佔帳戶總資金比例（0 = 關、用 risk/SL sizing）
    private readonly decimal _maxPortfolioExposurePct; // 所有倉 notional 總和 / 帳戶 上限（0 = 不限）
    private readonly bool    _disableLevSlCap;        // true = 關掉 leverage-aware SL cap、SL 不收緊到強平距離內（全倉抱反彈）
    private readonly int     _maxOpenPositions;  // 同時開倉硬上限（預設 3、user request 「3 倉不會再多」）

    /// <summary>
    /// Per-symbol 連續開倉冷卻時間（秒）。Strategy 訊號在門檻附近反翻時、會造成「進場-反向-再進場」
    /// 的 churn，commission + 滑價會吃光小利。預設 1800 (30 min)；
    /// env AUTOTRADER_MIN_ENTRY_INTERVAL_SEC 可調、設 0 完全關閉冷卻。
    /// 只擋 open_long / open_short（含 scale_in）；close / protect / reduce_only 不受影響。
    /// </summary>
    private readonly int _minEntryIntervalSec;
    private readonly ConcurrentDictionary<string, DateTime> _lastEntryAt = new();

    /// <summary>
    /// 新倉 symbol 跟「已開倉 symbol」最大允許 30-day daily-return correlation。
    /// > threshold 視為「實質同向、3 倉變 1 倉」、拒絕開新倉。
    /// 0 或負值 = 關閉檢查。預設 0.85（crypto major 之間 > 0.85 很常見、抓掉最嚴重的）。
    /// env: AUTOTRADER_MAX_CORRELATION
    /// </summary>
    private readonly decimal _maxCorrelation;

    /// <summary>
    /// 開倉前對 BingX funding rate 做 sanity check：
    ///   funding > +threshold% / 8h → 拒絕 LONG（持 long 要付 funding、太貴）
    ///   funding < -threshold% / 8h → 拒絕 SHORT（持 short 要付、太貴）
    /// 預設 threshold 0.05%（年化 ±54%）；0 = 關閉。env: AUTOTRADER_MAX_FUNDING_RATE_PCT
    /// </summary>
    private readonly decimal _maxFundingRatePct;
    private static readonly HttpClient _publicHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Liquidation warning threshold — distance &lt; this 推 Discord/LINE 但不平倉。
    /// 比 _perpLiqEmergencyPct（強制平倉）更早一階、給 user 手動 add margin 的機會。
    /// 預設 10%；0 = 關閉。env: AUTOTRADER_LIQ_ALERT_PCT
    /// </summary>
    private readonly decimal _liqAlertPct;

    /// <summary>
    /// C — Bracket SL：開倉時帶 exchange-side stop_loss_price（BingX atomic attach 到 position）。
    /// 解決「broker 軟 SL 在 broker downtime 時失效、20x 倉位裸奔」的風險。
    /// SL 價格 = entry × (1 ∓ InitialSlPct/100)（long 下方 / short 上方）、用 markPrice 近似 entry。
    ///
    /// 跟軟 SL 並存：exchange SL 是「災難底線」（broker 掛了才靠它）、broker 軟 SL（BE/trailing）
    /// 在正常運作時更緊、會先觸發。BingX position 平倉後會自動 cancel 附帶的 SL。
    /// 預設 off（opt-in、不改既有行為）。env: AUTOTRADER_BRACKET_SL_ENABLED=true 開啟。
    /// </summary>
    private readonly bool _bracketSlEnabled;

    /// <summary>
    /// finding D — perp 組合層「equity 從當日峰值回落」熔斷(跟 r16「跌破當日開盤」互補)。
    /// 預設 off(opt-in、比照 bracket SL「真錢危險、demo 驗證後再開」慣例,不偷改線上行為)。
    /// env: AUTOTRADER_PERP_DD_CB=1 開啟,閾值沿用 _maxPortfolioDdPct(AUTOTRADER_MAX_PORTFOLIO_DD_PCT、預設 8%)。
    /// </summary>
    private readonly bool _perpDdCbEnabled;

    /// <summary>
    /// C — Bracket TP（opt-in）：開倉時帶 exchange-side take_profit_price，到價自動止盈。
    /// TP 價 = entry × (1 ± TpPct/100)（long 上方 / short 下方）。0 / 沒設 → 關（讓利潤跑、走 trailing）。
    /// 跟 trailing 並存：trailing 鎖回撤、TP 鎖絕對目標、誰先到誰先平。env: AUTOTRADER_BRACKET_TP_PCT
    /// </summary>
    private readonly decimal _bracketTpPct;

    /// <summary>
    /// Bracket TP 的 R:R 倍數（opt-in）：>0 時 TP 距離 = RR × 槓桿感知 SL 距離，比寫死 % 更合理、
    /// 且因為 SL 已隨槓桿縮放、TP 也自動跟著縮。優先於 _bracketTpPct。0 / 沒設 → 退回固定 %。
    /// env: AUTOTRADER_BRACKET_TP_RR（例 2 = 賺賠比 2:1）
    /// </summary>
    private readonly decimal _bracketTpRr;

    /// <summary>
    /// #1 — Bracket SL sync（opt-in、預設 off）：broker 把軟 SL 移到 BE/trailing 時，順手把 exchange 端
    /// 的 stop 也 cancel+replace 到新價，downtime 期間也能鎖住已實現的利潤（而不是凍在開倉 entry−SL）。
    /// **真錢危險、必須先在 demo 帳號驗證 BingX 的 stop 取代行為再開**。env: AUTOTRADER_BRACKET_SL_SYNC_ENABLED=true
    /// </summary>
    private readonly bool _bracketSlSyncEnabled;

    /// <summary>
    /// #2 — 裸倉自動補 SL（opt-in、預設 off）：保護迴圈每 cycle 檢查、若某倉「交易所端沒掛任何 STOP」，
    /// 自動補一張到 broker 當前的軟 SL 價（state.SlPrice、已被 protection pass 驗過不會立即觸發）。
    /// 補丁對象包含「feature 上線前就開的裸倉」（如 XRP）跟「開倉時 bracket SL 沒掛成功」的漏網倉。
    /// **開啟會動到現有裸倉（含 XRP）**。env: AUTOTRADER_RETROFIT_NAKED_SL=true
    /// </summary>
    private readonly bool _retrofitNakedSl;

    /// <summary>
    /// Slippage audit threshold（% of mark price at signal time）。
    /// 開倉成交後 |filled - mark| / mark × 100 &gt; threshold → AddLog warn + 觸發 backoff（C1）。
    /// 0 = 關閉；預設 0.30%。env: AUTOTRADER_SLIPPAGE_ALERT_PCT
    /// </summary>
    private readonly decimal _slippageAlertPct;
    private readonly ConcurrentDictionary<string, DateTime> _lastLiqAlertAt = new();
    private static readonly TimeSpan _liqAlertCooldown = TimeSpan.FromHours(1);

    /// <summary>
    /// D1 Phase 1 — sizing 邏輯（confidence multiplier / Kelly cache / slippage backoff state）
    /// 抽到 AutoTraderSizingService。AutoTrader 透過 _sizing 呼叫。slippage observation 仍在
    /// AutoTrader（因為要寫 per-watch AddLog），但 backoff cache 在 _sizing。
    /// </summary>
    private readonly AutoTraderSizingService _sizing;

    /// <summary>
    /// 根據 strategy-worker 帶回的 regime 訊號做進場 gate。
    /// 模式：
    ///   "off"    — 完全不檢查（向後相容、預設）
    ///   "soft"   — 違反規則只 log warning、不擋單；給觀察期用
    ///   "strict" — 違反規則直接 skip、不開倉
    /// 規則：
    ///   HighVol     → 任何 open_* 都 skip（ATR &gt; 4%、易吃 SL）
    ///   Squeeze     → 任何 open_* 都 skip（BB 過窄、方向未明）
    ///   TrendingUp  → 只允許 open_long / scale_in_long、擋 open_short
    ///   TrendingDown→ 只允許 open_short / scale_in_short、擋 open_long
    ///   RangeBound / Unclear → 不擋（震盪策略 + 不明就交給 confidence 決定）
    /// env: AUTOTRADER_REGIME_GATE_MODE
    /// </summary>
    private readonly string _regimeGateMode;
    /// <summary>
    /// Perp 同方向 scale-in 門檻遞增步幅。已有 N 個同方向倉時、加倉需要 confidence ≥
    /// MinConfidence + N × Step。env AUTOTRADER_PERP_SCALE_IN_STEP 可調、預設 0.15。
    /// 設 0 = 永遠用 MinConfidence、等同允許無限加倉（不建議）；
    /// 設 1 = 已有 1 倉就鎖死（再強的訊號也不能加倉）。
    /// </summary>
    private readonly decimal _perpScaleInStep;
    public IReadOnlyDictionary<string, PerpetualPositionState> PerpetualPositionStates => _perpPositionState;
    public decimal PerpLiquidationEmergencyPct => _perpLiqEmergencyPct;
    public decimal PerpScaleInStep => _perpScaleInStep;

    /// <summary>申報資金錨定（per exchange）。risk gate 用 min(declared, live) 算 equity。</summary>
    private readonly ConcurrentDictionary<string, decimal> _declaredCapitalByExchange;
    public IReadOnlyDictionary<string, decimal> DeclaredCapital => _declaredCapitalByExchange;

    /// <summary>
    /// 由 BalanceAnchorService 偵測到 deposit / withdraw 後呼叫，原子更新該交易所的 anchor。
    /// 不在這裡決定要不要更新——caller 自己判斷 unexplained_delta 是否超過 threshold。
    /// 回傳前後值供 caller log + 推通知。
    /// </summary>
    public (decimal Old, decimal New) UpdateDeclaredCapital(string exchange, decimal newAnchor)
    {
        var key = exchange.ToLowerInvariant();
        var oldVal = _declaredCapitalByExchange.TryGetValue(key, out var v) ? v : 0m;
        _declaredCapitalByExchange[key] = newAnchor;
        _logger.LogInformation("RiskAnchor updated: {Exchange} {Old:F2} → {New:F2}", key, oldVal, newAnchor);
        return (oldVal, newAnchor);
    }

    /// <summary>
    /// 多用戶:per-(owner,exchange) 申報資金。key=「owner:exchange」。
    /// 未設 per-user 時 ResolveDeclaredCapital 會回退到全域「exchange」預設(向後相容)。
    /// </summary>
    public (decimal Old, decimal New) UpdateDeclaredCapital(string ownerPrincipalId, string exchange, decimal newAnchor)
    {
        var owner = string.IsNullOrEmpty(ownerPrincipalId) ? "prn_dashboard" : ownerPrincipalId;
        var key = $"{owner}:{exchange.ToLowerInvariant()}";
        var oldVal = _declaredCapitalByExchange.TryGetValue(key, out var v) ? v : 0m;
        _declaredCapitalByExchange[key] = newAnchor;
        _logger.LogInformation("RiskAnchor updated (per-user): {Key} {Old:F2} → {New:F2}", key, oldVal, newAnchor);
        return (oldVal, newAnchor);
    }

    /// <summary>
    /// (owner,exchange) → 申報資金上限。先找 per-user「owner:exchange」、
    /// 無則回退全域「exchange」預設(env Risk__DeclaredCapital__{ex} / BalanceAnchorService 維護的那個)。
    /// 回 0 = 未啟用、用實際 balance。
    /// </summary>
    public decimal ResolveDeclaredCapital(string ownerPrincipalId, string exchange)
    {
        var ex = exchange.ToLowerInvariant();
        if (!string.IsNullOrEmpty(ownerPrincipalId)
            && _declaredCapitalByExchange.TryGetValue($"{ownerPrincipalId}:{ex}", out var perUser) && perUser > 0m)
            return perUser;
        return _declaredCapitalByExchange.TryGetValue(ex, out var global) ? global : 0m;
    }

    // D1 Phase 2 — PerpProtectionAction / PerpProtectionDecision / EvaluatePerpetualProtection
    // 移到 ProtectionDecisionEngine.cs。Callsite 改用 ProtectionDecisionEngine.EvaluatePerpetualProtection。
#if D1_PHASE2_REMOVED_BODY  // 留 marker、不會編譯、之後找方便
    public static PerpProtectionDecision EvaluatePerpetualProtection(
        PerpetualPositionState state, decimal markPrice, decimal qty,
        decimal liqDistancePct, ProtectionConfig config, decimal liqEmergencyPct)
    {
        if (state.EntryPrice <= 0m || qty <= 0m || markPrice <= 0m)
            return new PerpProtectionDecision { Action = PerpProtectionAction.None, Reason = "invalid inputs" };

        var isLong = state.Side == "long";
        var pnlPct = isLong
            ? (markPrice - state.EntryPrice) / state.EntryPrice * 100m
            : (state.EntryPrice - markPrice) / state.EntryPrice * 100m;
        // Effective peak: long 取最高 mark、short 取最低 mark；防禦性 max/min。
        var effectivePeak = isLong
            ? Math.Max(state.PeakMark, markPrice)
            : (state.PeakMark > 0m ? Math.Min(state.PeakMark, markPrice) : markPrice);
        var peakPct = isLong
            ? (effectivePeak  - state.EntryPrice) / state.EntryPrice * 100m
            : (state.EntryPrice - effectivePeak)  / state.EntryPrice * 100m;

        // 1) Liquidation emergency — 不論方向、距離過近就先平
        if (state.LiquidationPrice > 0m && liqDistancePct > 0m && liqDistancePct <= liqEmergencyPct)
        {
            return new PerpProtectionDecision
            {
                Action = PerpProtectionAction.LiquidationEmergency,
                PartialQty = qty, PnlPct = pnlPct, LiqDistancePct = liqDistancePct,
                Reason = $"⚠ liquidation emergency: distance {liqDistancePct:F2}% ≤ {liqEmergencyPct}% (mark {markPrice:F4}, liq {state.LiquidationPrice:F4})",
            };
        }

        // 2) SL hit (含 BE 後挪過的 SL)
        var slHit = isLong ? markPrice <= state.SlPrice : markPrice >= state.SlPrice;
        if (slHit)
        {
            return new PerpProtectionDecision
            {
                Action = PerpProtectionAction.SlHit,
                PartialQty = qty, PnlPct = pnlPct, LiqDistancePct = liqDistancePct,
                Reason = $"SL hit ({state.Side}) @ {markPrice:F4} {(isLong ? "≤" : "≥")} {state.SlPrice:F4} (entry {state.EntryPrice:F4}, P&L {pnlPct:+0.00;-0.00}%)",
            };
        }

        // 3) Partial exit
        if (!state.PartialExited && pnlPct >= config.PartialExitPct)
        {
            var partialQty = Math.Round(qty * config.PartialExitRatio, 4, MidpointRounding.ToZero);
            if (partialQty > 0m && partialQty < qty)
            {
                return new PerpProtectionDecision
                {
                    Action = PerpProtectionAction.PartialExit,
                    PartialQty = partialQty, PnlPct = pnlPct, LiqDistancePct = liqDistancePct,
                    Reason = $"Partial exit ({state.Side}) @ +{pnlPct:F2}% — selling {config.PartialExitRatio:P0} ({partialQty})",
                };
            }
        }

        // 4) BE SL move (peak-based、學自對照組 commit 6f11aac)
        if (!state.BeMoved && peakPct >= config.BreakevenTriggerPct)
        {
            var newSl = isLong
                ? state.EntryPrice * (1m + config.BreakevenBufferPct / 100m)
                : state.EntryPrice * (1m - config.BreakevenBufferPct / 100m);
            // 只往「縮小風險」方向挪：long 往上挪、short 往下挪
            var moves = isLong ? newSl > state.SlPrice : newSl < state.SlPrice;
            if (moves)
            {
                return new PerpProtectionDecision
                {
                    Action = PerpProtectionAction.BeMove,
                    NewSlPrice = newSl, PnlPct = pnlPct, LiqDistancePct = liqDistancePct,
                    Reason = $"SL → BE ({state.Side}) {(isLong ? "+" : "−")}{config.BreakevenBufferPct}% (was {state.SlPrice:F4}, now {newSl:F4}) at peak +{peakPct:F2}%",
                };
            }
        }

        // 5) Trailing lock — peak gain 達門檻後、把 SL 拖移到 peak ± distance%
        if (config.TrailingTriggerPct > 0m && peakPct >= config.TrailingTriggerPct)
        {
            var trailSl = isLong
                ? effectivePeak * (1m - config.TrailingDistancePct / 100m)   // long: SL 在 peak 下方
                : effectivePeak * (1m + config.TrailingDistancePct / 100m);  // short: SL 在 peak 上方
            // 只往「縮小風險」方向動
            var moves = isLong ? trailSl > state.SlPrice : trailSl < state.SlPrice;
            if (moves)
            {
                return new PerpProtectionDecision
                {
                    Action = PerpProtectionAction.TrailingLock,
                    NewSlPrice = trailSl, PnlPct = pnlPct, LiqDistancePct = liqDistancePct,
                    Reason = $"Trailing lock ({state.Side}): SL {state.SlPrice:F4} → {trailSl:F4} (peak {effectivePeak:F4} {(isLong ? "−" : "+")}{config.TrailingDistancePct}%) at peak +{peakPct:F2}%",
                };
            }
        }

        return new PerpProtectionDecision { Action = PerpProtectionAction.None, PnlPct = pnlPct, LiqDistancePct = liqDistancePct };
    }
#endif

    // ── B3 SL flush freeze ─────────────────────────────────────────
    //
    // 連環 SL 觸發 = 訊號斷崖式失敗（策略當下抓不住行情、或極端 regime）。
    // 滑動視窗看最近 N 分鐘的 SL hit 次數，超過閾值就把 _enabled 翻 false、
    // 強制讓使用者手動 reset。避免「演算法亂跑、user 還沒注意到、損失越滾越大」。
    // SlHitRecord 已移到 ProtectionDecisionEngine.cs（同 namespace 引用不變）。
    private readonly int _slFlushThreshold;
    private readonly int _slFlushWindowMinutes;
    private readonly ConcurrentQueue<SlHitRecord> _recentSlHits = new();
    private DateTime? _slFlushTriggeredAt;

    // 2026-05-27 真錢安全強化:Circuit Breaker 觸發時 Discord critical alert
    // Dedup 規則:每 UTC 日 per-exchange 只推一次、但 DD 惡化 +2pp 會 escalation 再推
    private readonly ConcurrentDictionary<string, (DateTime At, decimal AlertedDd)> _cbAlertedState = new();

    public int SlFlushThreshold      => _slFlushThreshold;
    public int SlFlushWindowMinutes  => _slFlushWindowMinutes;
    public bool SlFlushTriggered     => _slFlushTriggeredAt.HasValue;
    public DateTime? SlFlushTriggeredAt => _slFlushTriggeredAt;
    public IReadOnlyList<SlHitRecord> RecentSlHits => _recentSlHits.ToArray();

    // D1 Phase 2 — ProtectionAction / ProtectionDecision / EvaluateProtection
    // 移到 ProtectionDecisionEngine.cs。Callsite 改用 ProtectionDecisionEngine.EvaluateProtection。
#if D1_PHASE2_REMOVED_BODY  // marker、不會編譯
    public static ProtectionDecision EvaluateProtection(
        PositionProtectionState state, decimal currentPrice, decimal qty, ProtectionConfig config)
    {
        if (state.EntryPrice <= 0m || qty <= 0m || currentPrice <= 0m)
            return new ProtectionDecision { Action = ProtectionAction.None, Reason = "invalid inputs" };

        var pnlPct  = (currentPrice    - state.EntryPrice) / state.EntryPrice * 100m;
        // Effective peak: max(stored peak, current)——防禦性算法、即使呼叫端忘了在 Evaluate
        // 前更新 state.PeakPrice、本函式仍正確。production handler 已會先 update。
        var effectivePeak = Math.Max(state.PeakPrice, currentPrice);
        var peakPct = (effectivePeak - state.EntryPrice) / state.EntryPrice * 100m;

        // 1) SL hit (含 BE 後挪過的 SL / trailing 上移過的 SL)
        if (currentPrice <= state.SlPrice)
        {
            return new ProtectionDecision
            {
                Action = ProtectionAction.SlHit,
                PartialQty = qty,
                PnlPct = pnlPct,
                Reason = $"SL hit @ {currentPrice:F4} ≤ {state.SlPrice:F4} (entry {state.EntryPrice:F4}, peak {state.PeakPrice:F4}, P&L {pnlPct:+0.00;-0.00}%)",
            };
        }

        // 2) Partial exit
        if (!state.PartialExited && pnlPct >= config.PartialExitPct)
        {
            var partialQty = Math.Round(qty * config.PartialExitRatio, 4, MidpointRounding.ToZero);
            if (partialQty > 0m && partialQty < qty)
            {
                return new ProtectionDecision
                {
                    Action = ProtectionAction.PartialExit,
                    PartialQty = partialQty,
                    PnlPct = pnlPct,
                    Reason = $"Partial exit @ +{pnlPct:F2}% — selling {config.PartialExitRatio:P0} ({partialQty})",
                };
            }
        }

        // 3) BE SL move (peak-based、不是 current pnl)
        if (!state.BeMoved && peakPct >= config.BreakevenTriggerPct)
        {
            var newSl = state.EntryPrice * (1m + config.BreakevenBufferPct / 100m);
            if (newSl > state.SlPrice)
            {
                return new ProtectionDecision
                {
                    Action = ProtectionAction.BeMove,
                    NewSlPrice = newSl,
                    PnlPct = pnlPct,
                    Reason = $"SL → BE +{config.BreakevenBufferPct}% (was {state.SlPrice:F4}, now {newSl:F4}) at peak +{peakPct:F2}%",
                };
            }
        }

        // 4) Trailing lock：peak gain 達門檻後、把 SL 拖移到 peak × (1 - distance%)
        if (config.TrailingTriggerPct > 0m && peakPct >= config.TrailingTriggerPct)
        {
            var trailSl = effectivePeak * (1m - config.TrailingDistancePct / 100m);
            if (trailSl > state.SlPrice)
            {
                return new ProtectionDecision
                {
                    Action = ProtectionAction.TrailingLock,
                    NewSlPrice = trailSl,
                    PnlPct = pnlPct,
                    Reason = $"Trailing lock: SL {state.SlPrice:F4} → {trailSl:F4} (peak {effectivePeak:F4} −{config.TrailingDistancePct}%) at peak +{peakPct:F2}%",
                };
            }
        }

        return new ProtectionDecision { Action = ProtectionAction.None, PnlPct = pnlPct };
    }
#endif

    private readonly ExchangeCredentialService? _credentials;

    // Lazy resolve 避免 circular DI：DiscordNotificationService 已依賴 AutoTraderService（snapshot），
    // 不能反向直接 inject、改透過 IServiceProvider runtime 拿。
    private readonly IServiceProvider _serviceProvider;
    private DiscordNotificationService? DiscordNotify => _serviceProvider.GetService<DiscordNotificationService>();
    private LineNotificationService? LineNotify => _serviceProvider.GetService<LineNotificationService>();

    public AutoTraderService(
        IExecutionDispatcher dispatcher,
        IWorkerRegistry registry,
        BrokerDb db,
        ILogger<AutoTraderService> logger,
        IServiceProvider serviceProvider,
        AutoTraderSizingService sizing,
        ExchangeCredentialService? credentials = null)
    {
        _dispatcher = dispatcher;
        _registry   = registry;
        _db         = db;
        _logger     = logger;
        _credentials = credentials;
        _serviceProvider = serviceProvider;
        _sizing = sizing;

        var forceRaw = Environment.GetEnvironmentVariable("AUTOTRADER_DEV_FORCE_ACTION")?.Trim().ToLowerInvariant();
        if (forceRaw == "buy" || forceRaw == "sell")
        {
            _devForceAction = forceRaw;
            _logger.LogWarning(
                "⚠ AUTOTRADER_DEV_FORCE_ACTION={Action} active — signals will be overridden, threshold bypassed. UNSET FOR PRODUCTION.",
                forceRaw);
        }

        _minConfidence = ParseMinConfidence(Environment.GetEnvironmentVariable("AUTOTRADER_MIN_CONFIDENCE"));
        _maxPortfolioDdPct = ParseMaxPortfolioDdPct(Environment.GetEnvironmentVariable("AUTOTRADER_MAX_PORTFOLIO_DD_PCT"));
        _protectionConfig = ProtectionDecisionEngine.ParseConfig();
        _slFlushThreshold = ParseIntEnv("AUTOTRADER_SL_FLUSH_THRESHOLD", defaultValue: 3, min: 1, max: 100);
        _slFlushWindowMinutes = ParseIntEnv("AUTOTRADER_SL_FLUSH_WINDOW_MINUTES", defaultValue: 60, min: 1, max: 1440);
        // Perp 強平距離保護：低於此 % 觸發 emergency close（不論 SL 是否到）。預設 5%
        _perpLiqEmergencyPct = ParsePctEnv("AUTOTRADER_PERP_LIQ_EMERGENCY_PCT", defaultValue: 5m, min: 0.5m, max: 50m);
        // 動態開倉本金：開倉前以「max_loss = balance × N% / SL%」算出 notional、再 / mark_price = qty
        // 預設 2% = 對齊 r14 max_loss_per_trade_pct。0 = 關閉（向後相容、用 watch.Quantity 固定值）
        _dynamicRiskPct = ParsePctEnv("AUTOTRADER_DYNAMIC_RISK_PCT", defaultValue: 2m, min: 0m, max: 10m);
        // Portfolio-level 累計風險上限。Per-trade 2% × 4 倉 = 8%、但只算單筆會破日損預算。
        // 預設 6% = 對齊 r16；新開倉若會推超這個總額、qty 自動縮（或縮到 0 略過）。
        _maxPortfolioRiskPct = ParsePctEnv("AUTOTRADER_MAX_PORTFOLIO_RISK_PCT", defaultValue: 6m, min: 0m, max: 30m);
        // ── 全倉（cross margin）曝險比例 sizing（opt-in、預設 0 = 關,用 risk/SL sizing）。
        //    notional = exposurePct% × anchoredBalance。判斷風險改看「總曝險 / 總資金」而非 per-trade SL。
        //    例:exposurePct=100 → 每倉 notional = 全部本金;=300 → 3 倍曝險(margin = notional/leverage)。
        //    回測證明 rsi_stoch / mfi 是「抱到反彈」的 edge(無停損),全倉 + 此模式比 leverage-aware tight SL 更貼近驗證過的行為。
        _exposurePct = ParsePctEnv("AUTOTRADER_EXPOSURE_PCT", defaultValue: 0m, min: 0m, max: 1000m);
        // 全倉模式組合曝險上限(所有倉 notional 總和 / balance)。0 = 不限。
        _maxPortfolioExposurePct = ParsePctEnv("AUTOTRADER_MAX_PORTFOLIO_EXPOSURE_PCT", defaultValue: 0m, min: 0m, max: 5000m);
        // 關掉 leverage-aware SL cap(opt-in、預設 false)。
        // 開啟後 SL 用 configured 值、不被收緊到強平距離內 —— cross margin 無 per-position 強平,可抱深回撤等反彈。
        _disableLevSlCap = string.Equals(
            Environment.GetEnvironmentVariable("AUTOTRADER_DISABLE_LEV_SL_CAP") ?? "false",
            "true", StringComparison.OrdinalIgnoreCase);
        // 同時最多幾個 open position（user request 「2+2+1~2%、3 倉不會再多」）。0 = 不限制（向後相容）
        _maxOpenPositions = (int)ParsePctEnv("AUTOTRADER_MAX_OPEN_POSITIONS", defaultValue: 3m, min: 0m, max: 20m);
        // Per-symbol 連續開倉冷卻：避免訊號反翻 churn、預設 30 分鐘
        _minEntryIntervalSec = (int)ParsePctEnv("AUTOTRADER_MIN_ENTRY_INTERVAL_SEC", defaultValue: 1800m, min: 0m, max: 86400m);
        // 30-day correlation cap：新倉 symbol 跟已開倉 symbol 高度同向就拒（防 3 倉變 1 倉）
        // 0 = 關閉、預設 0.85（crypto major 普遍 > 0.85、抓掉最嚴重的同 beta）
        _maxCorrelation = ParsePctEnv("AUTOTRADER_MAX_CORRELATION", defaultValue: 0.85m, min: 0m, max: 1m);
        // funding rate 上限（% / 8h）：超過此 abs 值拒絕對應方向開倉
        // 0 = 關閉、預設 0.05% (年化 ~±54%、捕捉 funding 異常擠擁)
        _maxFundingRatePct = ParsePctEnv("AUTOTRADER_MAX_FUNDING_RATE_PCT", defaultValue: 0.05m, min: 0m, max: 5m);
        // Liquidation warning（比 emergency 早一階、push 不平倉）
        _liqAlertPct = ParsePctEnv("AUTOTRADER_LIQ_ALERT_PCT", defaultValue: 10m, min: 0m, max: 50m);
        // C — Bracket SL（opt-in、預設 off 不改既有軟 SL 行為）
        _bracketSlEnabled = string.Equals(
            Environment.GetEnvironmentVariable("AUTOTRADER_BRACKET_SL_ENABLED") ?? "false",
            "true", StringComparison.OrdinalIgnoreCase);
        // finding D — perp portfolio DD 熔斷（opt-in、預設 off,比照 bracket SL 慣例；閾值用 _maxPortfolioDdPct）
        _perpDdCbEnabled = Environment.GetEnvironmentVariable("AUTOTRADER_PERP_DD_CB")
            is "1" or "true" or "True" or "TRUE" or "on" or "ON";
        // C — Bracket TP（opt-in、預設 0 = 關，讓利潤跑）
        _bracketTpPct = ParsePctEnv("AUTOTRADER_BRACKET_TP_PCT", defaultValue: 0m, min: 0m, max: 500m);
        // Bracket TP R:R 倍數（opt-in、優先於固定 %）；0 = 退回固定 %
        _bracketTpRr = ParsePctEnv("AUTOTRADER_BRACKET_TP_RR", defaultValue: 0m, min: 0m, max: 50m);
        // #1 — Bracket SL sync（opt-in、預設 off；真錢危險、demo 驗證後再開）
        _bracketSlSyncEnabled = string.Equals(
            Environment.GetEnvironmentVariable("AUTOTRADER_BRACKET_SL_SYNC_ENABLED") ?? "false",
            "true", StringComparison.OrdinalIgnoreCase);
        // #2 — 裸倉自動補 SL（opt-in、預設 off；開啟會動到現有裸倉含 XRP）
        _retrofitNakedSl = string.Equals(
            Environment.GetEnvironmentVariable("AUTOTRADER_RETROFIT_NAKED_SL") ?? "false",
            "true", StringComparison.OrdinalIgnoreCase);
        // Slippage audit threshold（filled_price vs signal-time markPrice）
        _slippageAlertPct = ParsePctEnv("AUTOTRADER_SLIPPAGE_ALERT_PCT", defaultValue: 0.30m, min: 0m, max: 10m);
        // D1 Phase 1: slippage backoff + confidence sizing + Kelly cache 全部移到 AutoTraderSizingService
        // （從 _sizing 讀回顯：env 各自有預設、constructor 已綁定）
        // Regime filter gate（HighVol / Squeeze / 方向錯誤 → skip）。預設 off 不破壞既有行為。
        var modeRaw = (Environment.GetEnvironmentVariable("AUTOTRADER_REGIME_GATE_MODE") ?? "off").Trim().ToLowerInvariant();
        _regimeGateMode = modeRaw is "soft" or "strict" ? modeRaw : "off";
        // Scale-in 步幅：每多 1 個同方向倉、required confidence 上升 N（預設 15%）
        var stepRaw = Environment.GetEnvironmentVariable("AUTOTRADER_PERP_SCALE_IN_STEP");
        _perpScaleInStep = decimal.TryParse(stepRaw, out var step) && step >= 0m && step <= 1m ? step : 0.15m;

        // 申報資金錨定（per exchange）：risk rules 用 min(declared, live_balance)，
        // 跌會收緊（正確）、漲不會放寬（user 要求）。0 = 不啟用、用實際 balance（向後相容）。
        // env 設定例：Risk__DeclaredCapital__Bingx=100 → BingX 即使後來賺到 200、risk 仍按 100 算。
        // 直到 user 主動調高、賺到的部分都不計入風控基底。
        _declaredCapitalByExchange = new ConcurrentDictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var ex in new[] { "Bingx", "Binance", "Alpaca" })
        {
            var raw = Environment.GetEnvironmentVariable($"Risk__DeclaredCapital__{ex}");
            if (decimal.TryParse(raw, out var amt) && amt > 0m)
                _declaredCapitalByExchange[ex.ToLowerInvariant()] = amt;
        }
        if (_declaredCapitalByExchange.Count > 0)
            _logger.LogInformation(
                "Risk capital anchors (from env, may be overridden by BalanceAnchorService DB restore): {Anchors}",
                string.Join(", ", _declaredCapitalByExchange.Select(kv => $"{kv.Key}={kv.Value:F2}")));
        _logger.LogInformation(
            "AutoTrader thresholds: confidence={Conf:P0} portfolio_dd={Dd}% sl_flush={Flush}/{Window}min · " +
            "protection: initial_sl={IniSl}% partial_exit={Pe}% (sell {Per:P0}) BE_trigger={Bet}% (buffer {Beb}%) · " +
            "perp_liq_emergency={LiqEm}%",
            _minConfidence, _maxPortfolioDdPct, _slFlushThreshold, _slFlushWindowMinutes,
            _protectionConfig.InitialSlPct, _protectionConfig.PartialExitPct, _protectionConfig.PartialExitRatio,
            _protectionConfig.BreakevenTriggerPct, _protectionConfig.BreakevenBufferPct,
            _perpLiqEmergencyPct);
        if (_exposurePct > 0m || _disableLevSlCap)
            _logger.LogWarning(
                "AutoTrader CROSS-MARGIN mode: exposure_pct={Exp}% portfolio_exposure_max={MaxExp}% disable_lev_sl_cap={Cap} " +
                "— sizing by notional/balance ratio, NOT per-trade SL; SL cap bypass lets positions ride deep drawdowns. " +
                "Ensure BingX margin mode is set to CROSS for these symbols.",
                _exposurePct, _maxPortfolioExposurePct, _disableLevSlCap);

        LoadWatchListFromDb();
        LoadSettingsFromDb();
        LoadPerpStatesFromDb();
    }

    internal static int ParseIntEnv(string envName, int defaultValue, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (!int.TryParse(raw.Trim(), out var v)) return defaultValue;
        if (v < min) return defaultValue;
        if (v > max) return max;
        return v;
    }

    /// <summary>
    /// 記錄一次 SL hit，並判斷是否觸發 flush（連環 SL → 凍結 auto-trader）。
    /// 滑動視窗外的舊 hit 會在這裡 prune；達到閾值就把 _enabled 翻 false、
    /// 寫 _slFlushTriggeredAt 給 dashboard 看（要 user 手動按 ResetSlFlush 復原）。
    /// </summary>
    internal void RecordSlHit(string exchange, string symbol, DateTime now)
    {
        _recentSlHits.Enqueue(new SlHitRecord { Exchange = exchange, Symbol = symbol, At = now });

        // 在 window 外的舊 hit 移除
        var cutoff = now.AddMinutes(-_slFlushWindowMinutes);
        while (_recentSlHits.TryPeek(out var oldest) && oldest.At < cutoff)
            _recentSlHits.TryDequeue(out _);

        // 達到閾值 → 凍結
        if (_slFlushTriggeredAt == null && _recentSlHits.Count >= _slFlushThreshold)
        {
            _slFlushTriggeredAt = now;
            _enabled = false;
            PersistSettings();   // SL flush 觸發的 disabled 也要持久化、避免重啟後又自己 enable 出去
            _logger.LogError(
                "⚠ SL flush triggered: {Count} SLs hit within {Window}min — auto-trader DISABLED. Manual reset required via /api/v1/auto-trader/sl-flush/reset.",
                _recentSlHits.Count, _slFlushWindowMinutes);
        }
    }

    /// <summary>手動清除 SL flush 狀態（呼叫 /api/v1/auto-trader/sl-flush/reset 後）。</summary>
    public void ResetSlFlush()
    {
        _slFlushTriggeredAt = null;
        while (_recentSlHits.TryDequeue(out _)) { }
        _logger.LogInformation("SL flush state reset (queue cleared, trigger flag cleared)");
    }

    // D1 Phase 2 — ParseProtectionConfig 移到 ProtectionDecisionEngine.ParseConfig()。
    // 內部 ctor 改 ProtectionDecisionEngine.ParseConfig() 呼叫、env 名稱不變。

    internal static decimal ParsePctEnv(string envName, decimal defaultValue, decimal min, decimal max)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (!decimal.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return defaultValue;
        if (v < min) return defaultValue;
        if (v > max) return max;
        return v;
    }

    internal static decimal ParseRatioEnv(string envName, decimal defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (!decimal.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return defaultValue;
        if (v <= 0m || v >= 1m) return defaultValue;  // (0, 1) 之外無意義
        return v;
    }

    internal static decimal ParseMinConfidence(string? raw)
    {
        const decimal defaultValue = 0.5m;
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (!decimal.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return defaultValue;
        if (v < 0m) return 0m;
        if (v > 1m) return 1m;
        return v;
    }

    internal static decimal ParseMaxPortfolioDdPct(string? raw)
    {
        const decimal defaultValue = 8m;
        if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
        if (!decimal.TryParse(raw.Trim(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v))
            return defaultValue;
        if (v <= 0m) return defaultValue;  // 0 / 負數視同沒設、走預設
        if (v > 100m) return 100m;
        return v;
    }

    /// <summary>
    /// 每 cycle 對 (exchange, currentPortfolioValue) 評估 circuit breaker。
    /// 內部會更新該 exchange 的 peak（漲就抬高、跨 UTC 午夜重置）並計算 DD%。
    /// 觸發條件：DD% ≥ _maxPortfolioDdPct → Triggered=true，呼叫端要 skip 該 cycle 的下單。
    /// </summary>
    // 2026-05-27 真錢安全強化:CB 觸發推 Discord(per-exchange dedup、UTC 日重置、惡化 +2pp escalation)
    // scopeKey = dedup/state key(spot=exchange、perp=owner:exchange);displayLabel = 訊息顯示用(可讀的 exchange)
    private async Task TryPushCbAlertAsync(string scopeKey, string displayLabel, CircuitBreakerEval cb, CancellationToken ct)
    {
        try
        {
            var nowUtc = DateTime.UtcNow;
            var today = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);
            bool shouldAlert = false;
            decimal alertedDd = cb.DdPct;
            _cbAlertedState.AddOrUpdate(scopeKey,
                _ => { shouldAlert = true; return (nowUtc, cb.DdPct); },
                (_, prev) =>
                {
                    if (prev.At < today) { shouldAlert = true; return (nowUtc, cb.DdPct); }    // UTC 新日:第一次推
                    if (cb.DdPct >= prev.AlertedDd + 2m) { shouldAlert = true; return (nowUtc, cb.DdPct); }  // 惡化 +2pp:escalation 再推
                    alertedDd = prev.AlertedDd;
                    return prev;
                });
            if (!shouldAlert) return;

            var discord = DiscordNotify;
            if (discord == null) { _logger.LogDebug("CB alert: Discord disabled, skip push"); return; }
            var body =
                $"⚠ **{displayLabel}** 當日 DD **{cb.DdPct:F2}%** ≥ 閾值 {cb.Threshold:F1}%、" +
                $"新開倉已暫停(既有部位的 SL / peak-trail 保護鏈仍會跑、不會撤單)。\n\n" +
                $"• Peak: `{cb.PeakValue:F2}`\n" +
                $"• Current: `{cb.CurrentValue:F2}`\n" +
                $"• Peak reset at: `{cb.PeakResetAt:yyyy-MM-dd HH:mm}` UTC\n\n" +
                $"**檢視:**dashboard `/trading-manage.html`\n" +
                $"**完全停止(切真錢全平):** `POST /api/v1/emergency/stop-all`";
            await discord.SendAdHocAsync(
                title: $"🚨 Circuit Breaker — {displayLabel} 當日 DD {cb.DdPct:F1}%",
                body: body,
                color: 0xF6465D,
                ct: ct);
            _logger.LogInformation("CB Discord alert sent for {Scope} DD={Dd:F2}%", scopeKey, cb.DdPct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoTrader: failed to push CB Discord alert for {Scope}", scopeKey);
        }
    }

    // scopeKey:spot 路徑傳 exchange("alpaca"/"binance");perp 路徑(finding D)傳 per-(owner:exchange)
    // 否則同 exchange 不同用戶的 equity 會共用同一個 peak、互相污染 DD(對齊 r16 的 owner:exchange:date 隔離)。
    public CircuitBreakerEval EvaluateCircuitBreaker(string scopeKey, decimal currentValue, DateTime nowUtc)
    {
        if (currentValue <= 0m)
        {
            // 沒有有效 portfolio value（worker 連不上、或帳戶為空）→ 視為 0 DD、不擋
            return new CircuitBreakerEval { Triggered = false, PeakValue = 0m, CurrentValue = 0m, DdPct = 0m, Threshold = _maxPortfolioDdPct };
        }

        var todayUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);
        var state = _peakByExchange.AddOrUpdate(
            scopeKey,
            _ => new PortfolioPeakState
            {
                PeakValue = currentValue, PeakResetAt = todayUtc,
                LastValue = currentValue, LastDdPct = 0m, LastTriggered = false, LastUpdate = nowUtc,
            },
            (_, s) =>
            {
                // UTC 午夜重置 peak
                if (s.PeakResetAt < todayUtc)
                {
                    s.PeakValue = currentValue;
                    s.PeakResetAt = todayUtc;
                }
                else if (currentValue > s.PeakValue)
                {
                    s.PeakValue = currentValue;
                }
                s.LastValue = currentValue;
                s.LastUpdate = nowUtc;
                return s;
            });

        var ddPct = state.PeakValue > 0m ? (state.PeakValue - currentValue) / state.PeakValue * 100m : 0m;
        var triggered = ddPct >= _maxPortfolioDdPct;
        state.LastDdPct = Math.Round(ddPct, 2);
        state.LastTriggered = triggered;

        return new CircuitBreakerEval
        {
            Triggered    = triggered,
            PeakValue    = state.PeakValue,
            CurrentValue = currentValue,
            DdPct        = Math.Round(ddPct, 2),
            Threshold    = _maxPortfolioDdPct,
            PeakResetAt  = state.PeakResetAt,
        };
    }

    // ── 持久化 ──────────────────────────────────────────────────────

    private void LoadWatchListFromDb()
    {
        try
        {
            var entries = _db.GetAll<AutoTradeWatchEntry>();
            foreach (var e in entries)
            {
                _watchList[e.EntryKey] = new WatchItem
                {
                    Symbol = e.Symbol, Exchange = e.Exchange, Strategy = e.Strategy,
                    Quantity = e.Quantity, Active = e.Active,
                    LastSignal = e.LastSignal, LastConfidence = e.LastConfidence, LastCheck = e.LastCheck,
                    Mode = string.IsNullOrEmpty(e.Mode) ? "spot" : e.Mode,
                    Leverage = e.Leverage > 0 ? e.Leverage : 5,
                    OwnerPrincipalId = string.IsNullOrEmpty(e.OwnerPrincipalId) ? "prn_dashboard" : e.OwnerPrincipalId,
                    HtfInterval = string.IsNullOrEmpty(e.HtfInterval) ? null : e.HtfInterval,
                    Shadow = e.Shadow,
                    BudgetPct = e.BudgetPct,
                    UseSignalSl = e.UseSignalSl,
                };
            }
            if (entries.Count > 0)
                _logger.LogInformation("AutoTrader: restored {Count} watch entries from DB", entries.Count);
        }
        catch (Exception ex)
        {
            // 不要因為 DB load 失敗就讓服務起不來——既有記憶體 dict 為空，至少 broker 起得來
            _logger.LogError(ex, "AutoTrader: failed to load watchlist from DB; starting with empty list");
        }
    }

    // 2026-05-27:加 onlyLastCheck 參數修「sweep 把 SQL UPDATE Active=0 反轉成 1」bug
    //   - 預設(false)= 完整 upsert,給 AddWatch / Resume / Pause / SetShadow / risk 調量等「明確意圖」呼叫
    //   - true = sweep 路徑、只更新 LastSignal / LastConfidence / LastCheck / UpdatedAt 三欄
    //     不再覆寫 Active / Shadow / Mode / Strategy / Quantity 等「狀態」欄
    //   根因:in-memory _watchList[*].Active 跟 DB 不同步時、sweep 每 cycle 把 in-memory 寫回 DB、
    //   會把外部直接 SQL UPDATE 的變更(active=0)反轉成 active=1。詳見 [[project_scanner_p0_option_c]] 的 mystery section。
    private void PersistWatch(string key, WatchItem item, bool onlyLastCheck = false)
    {
        try
        {
            var existing = _db.Get<AutoTradeWatchEntry>(key);
            var now = DateTime.UtcNow;
            if (existing == null)
            {
                _db.Insert(new AutoTradeWatchEntry
                {
                    EntryKey = key, Symbol = item.Symbol, Exchange = item.Exchange,
                    Strategy = item.Strategy, Quantity = item.Quantity, Active = item.Active,
                    LastSignal = item.LastSignal, LastConfidence = item.LastConfidence, LastCheck = item.LastCheck,
                    Mode = item.Mode, Leverage = item.Leverage,
                    OwnerPrincipalId = item.OwnerPrincipalId,
                    HtfInterval = item.HtfInterval,
                    Shadow = item.Shadow,
                    BudgetPct = item.BudgetPct,
                    CreatedAt = now, UpdatedAt = now,
                });
            }
            else if (onlyLastCheck)
            {
                // Sweep 路徑:只動 last-check 欄;Active / Shadow / Strategy 等保留 DB 既有值
                existing.LastSignal = item.LastSignal;
                existing.LastConfidence = item.LastConfidence;
                existing.LastCheck = item.LastCheck;
                existing.UpdatedAt = now;
                _db.Update(existing);
            }
            else
            {
                existing.Symbol = item.Symbol; existing.Exchange = item.Exchange;
                existing.Strategy = item.Strategy; existing.Quantity = item.Quantity; existing.Active = item.Active;
                existing.LastSignal = item.LastSignal; existing.LastConfidence = item.LastConfidence; existing.LastCheck = item.LastCheck;
                existing.Mode = item.Mode; existing.Leverage = item.Leverage;
                existing.OwnerPrincipalId = item.OwnerPrincipalId;
                existing.HtfInterval = item.HtfInterval;
                existing.Shadow = item.Shadow;
                existing.BudgetPct = item.BudgetPct;
                existing.UpdatedAt = now;
                _db.Update(existing);
            }
        }
        catch (Exception ex)
        {
            // 持久化失敗不要中斷主流程——記憶體 dict 已更新、log 出來等下次 add/remove 重試
            _logger.LogWarning(ex, "AutoTrader: failed to persist watch entry {Key}", key);
        }
    }

    private void DeletePersistedWatch(string key)
    {
        try { _db.Delete<AutoTradeWatchEntry>(key); }
        catch (Exception ex) { _logger.LogWarning(ex, "AutoTrader: failed to delete persisted watch {Key}", key); }
    }

    // ── 全域設定持久化（enabled / interval_seconds）──
    private const string SettingsKey = "main";

    private void LoadSettingsFromDb()
    {
        try
        {
            var entry = _db.Get<AutoTraderSettingsEntry>(SettingsKey);
            if (entry != null)
            {
                _enabled = entry.Enabled;
                _intervalSeconds = Math.Max(60, entry.IntervalSeconds);
                _logger.LogInformation(
                    "AutoTrader: restored settings from DB (enabled={Enabled}, interval={Interval}s)",
                    _enabled, _intervalSeconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoTrader: failed to load settings; using defaults");
        }
    }

    private void PersistSettings()
    {
        try
        {
            var existing = _db.Get<AutoTraderSettingsEntry>(SettingsKey);
            var now = DateTime.UtcNow;
            if (existing == null)
            {
                _db.Insert(new AutoTraderSettingsEntry
                {
                    SingletonKey = SettingsKey, Enabled = _enabled,
                    IntervalSeconds = _intervalSeconds, UpdatedAt = now,
                });
            }
            else
            {
                existing.Enabled = _enabled;
                existing.IntervalSeconds = _intervalSeconds;
                existing.UpdatedAt = now;
                _db.Update(existing);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoTrader: failed to persist settings");
        }
    }

    // ── Perp 部位保護狀態持久化 ──
    private void LoadPerpStatesFromDb()
    {
        try
        {
            var entries = _db.GetAll<PerpetualPositionStateEntry>();
            foreach (var e in entries)
            {
                _perpPositionState[e.EntryKey] = new PerpetualPositionState
                {
                    OwnerPrincipalId = string.IsNullOrEmpty(e.OwnerPrincipalId) ? "prn_dashboard" : e.OwnerPrincipalId,
                    Exchange = e.Exchange, Symbol = e.Symbol, Side = e.Side,
                    EntryPrice = e.EntryPrice, PeakMark = e.PeakMark, SlPrice = e.SlPrice,
                    LiquidationPrice = e.LiquidationPrice, Leverage = e.Leverage,
                    PartialExited = e.PartialExited, BeMoved = e.BeMoved,
                    CreatedAt = e.CreatedAt, UpdatedAt = e.UpdatedAt,
                };
            }
            if (entries.Count > 0)
                _logger.LogInformation("AutoTrader: restored {Count} perp position states from DB", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoTrader: failed to load perp position states");
        }
    }

    private void PersistPerpState(string key, PerpetualPositionState state)
    {
        try
        {
            var existing = _db.Get<PerpetualPositionStateEntry>(key);
            if (existing == null)
            {
                _db.Insert(new PerpetualPositionStateEntry
                {
                    EntryKey = key, OwnerPrincipalId = state.OwnerPrincipalId,
                    Exchange = state.Exchange, Symbol = state.Symbol, Side = state.Side,
                    EntryPrice = state.EntryPrice, PeakMark = state.PeakMark, SlPrice = state.SlPrice,
                    LiquidationPrice = state.LiquidationPrice, Leverage = state.Leverage,
                    PartialExited = state.PartialExited, BeMoved = state.BeMoved,
                    CreatedAt = state.CreatedAt, UpdatedAt = state.UpdatedAt,
                });
            }
            else
            {
                existing.OwnerPrincipalId = state.OwnerPrincipalId;
                existing.EntryPrice = state.EntryPrice; existing.PeakMark = state.PeakMark;
                existing.SlPrice = state.SlPrice; existing.LiquidationPrice = state.LiquidationPrice;
                existing.Leverage = state.Leverage;
                existing.PartialExited = state.PartialExited; existing.BeMoved = state.BeMoved;
                existing.UpdatedAt = state.UpdatedAt;
                _db.Update(existing);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoTrader: failed to persist perp state {Key}", key);
        }
    }

    private void DeletePersistedPerpState(string key)
    {
        try { _db.Delete<PerpetualPositionStateEntry>(key); }
        catch (Exception ex) { _logger.LogWarning(ex, "AutoTrader: failed to delete perp state {Key}", key); }
    }

    // ── 外部控制 API ────────────────────────────────────────────────

    public bool IsEnabled => _enabled;
    public int IntervalSeconds => _intervalSeconds;
    public IReadOnlyDictionary<string, WatchItem> WatchList => _watchList;
    public IEnumerable<TradeLog> RecentLogs => _tradeLog.ToArray().Take(MaxLogEntries);

    public void Enable() { _enabled = true; PersistSettings(); _logger.LogInformation("AutoTrader ENABLED"); }
    public void Disable() { _enabled = false; PersistSettings(); _logger.LogInformation("AutoTrader DISABLED"); }
    public void SetInterval(int seconds) { _intervalSeconds = Math.Max(60, seconds); PersistSettings(); }

    public void AddWatch(string symbol, string exchange, string strategy = "composite", decimal quantity = 1,
        string mode = "spot", int leverage = 5, string ownerPrincipalId = "prn_dashboard",
        string? htfInterval = null, bool shadow = false)
    {
        var key = $"{exchange}:{symbol}";
        var validModes = new[] { "spot", "perp_long_only", "perp_both" };
        if (!validModes.Contains(mode)) mode = "spot";
        leverage = Math.Max(1, Math.Min(125, leverage));
        // 過濾 HTF 字串：明顯不合法（過長 / 控制字元）就吃成 null
        var cleanedHtf = string.IsNullOrWhiteSpace(htfInterval) ? null : htfInterval.Trim();
        if (cleanedHtf != null && cleanedHtf.Length > 10) cleanedHtf = null;
        var item = new WatchItem
        {
            Symbol = symbol, Exchange = exchange, Strategy = strategy,
            Quantity = quantity, Active = true, Mode = mode, Leverage = leverage,
            OwnerPrincipalId = string.IsNullOrEmpty(ownerPrincipalId) ? "prn_dashboard" : ownerPrincipalId,
            HtfInterval = cleanedHtf, Shadow = shadow,
        };
        _watchList[key] = item;
        PersistWatch(key, item);
        _logger.LogInformation("AutoTrader: watching {Key} strategy={Strategy} qty={Qty} mode={Mode} lev={Lev}x htf={Htf} shadow={Shadow} owner={Owner}",
            key, strategy, quantity, mode, leverage, cleanedHtf ?? "-", shadow, item.OwnerPrincipalId);
    }

    /// <summary>
    /// 移除 watch。Phase A2：傳 requesterPrincipalId + isAdmin、只允許 owner 自己或 admin 刪。
    /// 回傳 (removed, reason)：reason="not_found" / "forbidden" / "" 成功。
    /// 老 caller 不傳會 backward-compat：requesterPrincipalId=null 視為 admin（單機開發用）。
    /// </summary>
    public (bool Removed, string Reason) RemoveWatch(string symbol, string exchange,
        string? requesterPrincipalId = null, bool isAdmin = false)
    {
        var key = $"{exchange}:{symbol}";
        if (!_watchList.TryGetValue(key, out var existing))
            return (false, "not_found");

        // null requester = legacy 路徑（無 auth context）→ 允許；正常 user 走 endpoint 一定有 requester
        if (requesterPrincipalId != null && !isAdmin
            && !string.Equals(existing.OwnerPrincipalId, requesterPrincipalId, StringComparison.Ordinal))
        {
            return (false, "forbidden");
        }

        var removed = _watchList.TryRemove(key, out _);
        if (removed) DeletePersistedWatch(key);
        return (removed, "");
    }

    public void PauseWatch(string symbol, string exchange)
    {
        var key = $"{exchange}:{symbol}";
        if (_watchList.TryGetValue(key, out var item))
        {
            item.Active = false;
            PersistWatch(key, item);
        }
    }

    public void ResumeWatch(string symbol, string exchange)
    {
        var key = $"{exchange}:{symbol}";
        if (_watchList.TryGetValue(key, out var item))
        {
            item.Active = true;
            PersistWatch(key, item);
        }
    }

    /// <summary>
    /// 切換既有 watch 的 shadow（影子）旗標。shadow=false = 轉真錢 live、shadow=true = 退回只記錄不下單。
    /// 轉真錢是「真錢武裝」動作 → endpoint 端會經確認框 + 受 KillSwitch/ReadOnly 閘控管。
    /// 權限模型同 RemoveWatch：只有 owner 或 admin 能改；requesterPrincipalId=null = legacy 視同 admin。
    /// 回傳 (ok, reason)：reason="not_found"/"forbidden"/"" 成功。
    /// </summary>
    public (bool Ok, string Reason) SetShadow(string symbol, string exchange, bool shadow,
        string? requesterPrincipalId = null, bool isAdmin = false)
    {
        var key = $"{exchange}:{symbol}";
        if (!_watchList.TryGetValue(key, out var existing))
            return (false, "not_found");

        if (requesterPrincipalId != null && !isAdmin
            && !string.Equals(existing.OwnerPrincipalId, requesterPrincipalId, StringComparison.Ordinal))
        {
            return (false, "forbidden");
        }

        existing.Shadow = shadow;
        PersistWatch(key, existing);
        _logger.LogInformation("AutoTrader: {Key} shadow -> {Shadow} (by {Req})",
            key, shadow, requesterPrincipalId ?? "legacy");
        return (true, "");
    }

    // ── 主迴圈 ──────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("AutoTrader service started (disabled by default, use API to enable)");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct);
            }
            catch (OperationCanceledException) { break; }

            if (!_enabled || _watchList.IsEmpty) continue;

            // 標記本次 cycle 開始時間——觀察性指標 / heartbeat watchdog 用，
            // 看 auto-trader 是否真的在跑（沒在跑時 dashboard / Discord 可警告）
            _lastCycleAt = DateTime.UtcNow;

            // Step 0a: Spot position protection sweep——既有的長倉保護
            try { await SweepPositionProtectionAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "AutoTrader spot protection sweep failed"); }

            // Step 0b: Perpetual position protection sweep（Phase 4）——
            // 雙向部位 SL math + 強平距離保護。BingX 等 perp exchange 才跑。
            try { await SweepPerpetualProtectionAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "AutoTrader perp protection sweep failed"); }

            foreach (var (key, item) in _watchList)
            {
                if (!item.Active || ct.IsCancellationRequested) continue;

                // ★ Failure backoff：同 watch 連續 N 次錯誤 → 暫停 1 小時
                if (_watchFailureState.TryGetValue(key, out var fs) &&
                    fs.ConsecutiveErrors >= MaxConsecutiveErrors)
                {
                    var sinceLastErr = DateTime.UtcNow - fs.LastErrorAt;
                    if (sinceLastErr < FailureBackoffWindow)
                    {
                        // 仍在 cooldown、靜默 skip（不再 AddLog 否則自循環）
                        continue;
                    }
                    // backoff 結束、試一次（fs 不清掉、若這次又錯仍累計）
                    _logger.LogInformation("AutoTrader retry after backoff: {Key} (errors={N}, last={Reason})",
                        key, fs.ConsecutiveErrors, fs.LastReason);
                }

                try
                {
                    await ProcessSymbolAsync(item, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AutoTrader error processing {Key}", key);
                    AddLog(item, "error", $"Exception: {ex.Message}");
                }
            }

            // Step 1c: Portfolio Scanner Hybrid pass(2026-05-27 Phase 1 Step B)。
            // 核心腿掃完後、再跑 scanner legs:策略 + 候選幣池、AutoTrader 挑訊號最強的開倉。
            // 預設所有 scanner 都 shadow=true、4 週紙交易達標才升 live。
            try { await SweepScannerLegsAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "AutoTrader scanner sweep failed"); }
        }
    }

    // ── B4a Position protection sweep ──────────────────────────────
    //
    // 對所有 watch 出現過的 exchange，拉 get_positions、對每個有 qty 的部位：
    //   1. 沒 state 的（新部位）→ 建 state、entry/peak/sl 從 avg_entry_price 算
    //   2. SL hit → 賣全部
    //   3. PnL ≥ partial_exit_pct 且未 partial_exited → 賣 ratio 比例
    //   4. PnL ≥ breakeven_trigger_pct 且未 be_moved → SL 上挪到 entry × (1 + buffer%)
    // 完成後清除 state 裡 qty=0 的舊部位。
    private async Task SweepPositionProtectionAsync(CancellationToken ct)
    {
        if (!_registry.HasAvailableWorker("trading.account") || !_registry.HasAvailableWorker("trading.order"))
            return;

        var exchanges = _watchList.Values.Select(w => w.Exchange).Distinct().ToList();
        foreach (var exchange in exchanges)
        {
            if (ct.IsCancellationRequested) return;
            var posResult = await _dispatcher.DispatchAsync(BuildRequest("trading.account", "get_positions",
                JsonSerializer.Serialize(new { exchange })));
            if (!posResult.Success) continue;

            var pos = JsonDocument.Parse(posResult.ResultPayload ?? "{}").RootElement;
            if (!pos.TryGetProperty("positions", out var posArr) || posArr.ValueKind != JsonValueKind.Array)
                continue;

            var liveKeys = new HashSet<string>();
            foreach (var p in posArr.EnumerateArray())
            {
                var symbol = p.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
                var qty = p.TryGetProperty("quantity", out var q) ? q.GetDecimal() : 0m;
                var entryPrice = p.TryGetProperty("avg_entry_price", out var e) ? e.GetDecimal() : 0m;
                var currentPrice = p.TryGetProperty("current_price", out var cp) ? cp.GetDecimal() : 0m;
                if (string.IsNullOrEmpty(symbol) || qty <= 0m || entryPrice <= 0m || currentPrice <= 0m) continue;

                liveKeys.Add($"{exchange}:{symbol}");
                try
                {
                    await ProcessPositionProtectionAsync(exchange, symbol, qty, entryPrice, currentPrice, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Protection check failed for {Exchange}:{Symbol}", exchange, symbol);
                }
            }

            // 清除 state 裡 exchange 下、live 沒出現的（已被全平的）
            var staleKeys = _positionState
                .Where(kv => kv.Key.StartsWith(exchange + ":") && !liveKeys.Contains(kv.Key))
                .Select(kv => kv.Key).ToList();
            foreach (var k in staleKeys)
            {
                if (_positionState.TryRemove(k, out _))
                    _logger.LogInformation("Position {Key} closed — protection state cleaned", k);
            }
        }
    }

    private async Task ProcessPositionProtectionAsync(
        string exchange, string symbol, decimal qty, decimal entryPrice, decimal currentPrice, CancellationToken ct)
    {
        var key = $"{exchange}:{symbol}";
        var now = DateTime.UtcNow;
        var state = _positionState.GetOrAdd(key, _ =>
        {
            var initialSl = entryPrice * (1m - _protectionConfig.InitialSlPct / 100m);
            _logger.LogInformation(
                "Protection state init for {Key}: entry={Entry:F4}, SL={Sl:F4} ({Pct}% below entry)",
                key, entryPrice, initialSl, _protectionConfig.InitialSlPct);
            return new PositionProtectionState
            {
                Exchange = exchange, Symbol = symbol,
                EntryPrice = entryPrice, PeakPrice = currentPrice,
                SlPrice = initialSl, PartialExited = false, BeMoved = false,
                CreatedAt = now, UpdatedAt = now,
            };
        });

        // 若交易所那邊 avg_entry_price 變了（比如加倉），更新 entry / 重置 SL
        if (state.EntryPrice != entryPrice && entryPrice > 0m)
        {
            _logger.LogInformation("Entry price changed for {Key}: {Old:F4} → {New:F4} — recomputing SL",
                key, state.EntryPrice, entryPrice);
            state.EntryPrice = entryPrice;
            state.SlPrice = entryPrice * (1m - _protectionConfig.InitialSlPct / 100m);
            state.PartialExited = false;
            state.BeMoved = false;
            state.PeakPrice = currentPrice;
        }

        if (currentPrice > state.PeakPrice) state.PeakPrice = currentPrice;
        state.UpdatedAt = now;

        var decision = ProtectionDecisionEngine.EvaluateProtection(state, currentPrice, qty, _protectionConfig);
        switch (decision.Action)
        {
            case ProtectionAction.SlHit:
                await ExecuteProtectionOrderAsync(exchange, symbol, "sell", qty, decision.Reason, ct);
                // 記入 SL flush 滑動視窗——夠多 SL 在窗內就把 _enabled 翻 false
                RecordSlHit(exchange, symbol, now);
                // state 在下次 sweep 因 qty=0 自動清掉
                break;

            case ProtectionAction.PartialExit:
            {
                var ok = await ExecuteProtectionOrderAsync(exchange, symbol, "sell", decision.PartialQty, decision.Reason, ct);
                if (ok) state.PartialExited = true;
                break;
            }

            case ProtectionAction.BeMove:
                _logger.LogInformation("BE SL move for {Key}: {Reason}", key, decision.Reason);
                state.SlPrice = decision.NewSlPrice;
                state.BeMoved = true;
                if (_watchList.TryGetValue(key, out var wi))
                    AddLog(wi, "protect", decision.Reason);
                break;

            case ProtectionAction.TrailingLock:
                _logger.LogInformation("Trailing lock for {Key}: {Reason}", key, decision.Reason);
                state.SlPrice = decision.NewSlPrice;  // 只往上、由 EvaluateProtection 保證
                if (_watchList.TryGetValue(key, out var wiTrail))
                    AddLog(wiTrail, "protect", decision.Reason);
                break;

            case ProtectionAction.None:
            default:
                break;
        }
    }

    /// <summary>下保護單（partial exit / SL hit），回傳是否成功送出。</summary>
    private async Task<bool> ExecuteProtectionOrderAsync(
        string exchange, string symbol, string side, decimal qty, string reason, CancellationToken ct)
    {
        var key = $"{exchange}:{symbol}";
        var clientOrderId = $"prot-{exchange}-{symbol}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}".Replace(".", "_");
        if (clientOrderId.Length > 36) clientOrderId = clientOrderId.Substring(0, 36);

        var orderPayload = JsonSerializer.Serialize(new
        {
            exchange, symbol, side, quantity = qty,
            order_type = "market",
            client_order_id = clientOrderId,
        });
        var result = await _dispatcher.DispatchAsync(BuildRequest("trading.order", "place_order", orderPayload));
        if (result.Success)
        {
            _logger.LogInformation(
                "🛡 Protection order placed: {Symbol} {Side} {Qty} on {Exchange} — {Reason}",
                symbol, side, qty, exchange, reason);
            if (_watchList.TryGetValue(key, out var wi))
                AddLog(wi, "protect", $"{side.ToUpper()} {qty} — {reason}");
            return true;
        }
        _logger.LogWarning(
            "Protection order failed: {Symbol} {Side} {Qty} on {Exchange} — {Error}",
            symbol, side, qty, exchange, result.ErrorMessage);
        if (_watchList.TryGetValue(key, out var wi2))
            AddLog(wi2, "error", $"Protection {side} failed: {result.ErrorMessage}");
        return false;
    }

    // ── Phase 4: Perpetual protection sweep ──────────────────────────
    //
    // 對 watch list 裡每個 (owner, exchange) pair 各自跑保護鏈、用該 owner 自己的 BingX credential。
    // state key = {owner}:{exchange}:{symbol}:{side}——同 symbol 雙向（hedge）+ 不同 user 各自獨立。
    private async Task SweepPerpetualProtectionAsync(CancellationToken ct)
    {
        if (!_registry.HasAvailableWorker("trading.perpetual")) return;

        // 從 watch list 找出所有 (owner, exchange) pairs
        var pairs = _watchList.Values
            .Where(w => w.Mode == "perp_long_only" || w.Mode == "perp_both")
            .Select(w => (Owner: w.OwnerPrincipalId, Exchange: w.Exchange))
            .Distinct()
            .ToList();
        if (pairs.Count == 0) return;

        foreach (var (owner, exchange) in pairs)
        {
            if (ct.IsCancellationRequested) return;

            // 帶 user credential 抓他 / 她自己的 positions（沒設則 fallback env 預設）
            var creds = BuildCredentialsObject(owner, exchange);
            var getPosPayload = creds == null
                ? JsonSerializer.Serialize(new { exchange })
                : JsonSerializer.Serialize(new { exchange, __credentials = creds });
            var posResult = await _dispatcher.DispatchAsync(BuildRequest("trading.perpetual", "get_positions", getPosPayload));
            if (!posResult.Success) continue;

            var pos = JsonDocument.Parse(posResult.ResultPayload ?? "{}").RootElement;
            if (!pos.TryGetProperty("positions", out var posArr) || posArr.ValueKind != JsonValueKind.Array)
                continue;

            var stateKeyPrefix = $"{owner}:{exchange}:";
            var liveKeys = new HashSet<string>();
            foreach (var p in posArr.EnumerateArray())
            {
                var symbol = p.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
                var side = p.TryGetProperty("side", out var sd) ? sd.GetString() ?? "long" : "long";
                var qty = p.TryGetProperty("quantity", out var q) ? q.GetDecimal() : 0m;
                var entryPrice = p.TryGetProperty("avg_entry_price", out var e) ? e.GetDecimal() : 0m;
                var markPrice = p.TryGetProperty("mark_price", out var mp) ? mp.GetDecimal() : 0m;
                var liqPrice = p.TryGetProperty("liquidation_price", out var lp) ? lp.GetDecimal() : 0m;
                var liqDist = p.TryGetProperty("liquidation_distance_pct", out var ld) ? ld.GetDecimal() : 0m;
                var leverage = p.TryGetProperty("leverage", out var lv) && lv.TryGetInt32(out var lvI) ? lvI : 1;
                if (string.IsNullOrEmpty(symbol) || qty <= 0m || entryPrice <= 0m || markPrice <= 0m) continue;

                var key = $"{owner}:{exchange}:{symbol}:{side}";
                liveKeys.Add(key);
                try
                {
                    await ProcessPerpProtectionAsync(owner, exchange, symbol, side, qty, entryPrice, markPrice,
                        liqPrice, liqDist, leverage, ct);

                    // #2 — 裸倉自動補 SL：用 protection pass 算好的軟 SL 價（state.SlPrice）。
                    // 放在 ProcessPerpProtection 之後、state 已建立/更新；倉若已被平掉就不在 _perpPositionState。
                    if (_retrofitNakedSl && _perpPositionState.TryGetValue(key, out var st) && st.SlPrice > 0m)
                        await RetrofitNakedStopAsync(owner, exchange, symbol, side, qty, st.SlPrice, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Perp protection check failed for {Key}", key);
                }
            }

            // 清掉這個 (owner, exchange) 下、live 沒出現的 state（已全平）
            var staleKeys = _perpPositionState
                .Where(kv => kv.Key.StartsWith(stateKeyPrefix, StringComparison.Ordinal) && !liveKeys.Contains(kv.Key))
                .Select(kv => kv.Key).ToList();
            foreach (var k in staleKeys)
            {
                if (_perpPositionState.TryRemove(k, out _))
                {
                    DeletePersistedPerpState(k);
                    _signalSlByKey.TryRemove(k, out _);   // 2026-06-04:平倉清掉橋接的 signal stop
                    _scaleInCountByKey.TryRemove(k, out _);   // finding J:全平 → 重置 scale-in 累計次數
                    _logger.LogInformation("Perp position {Key} closed — protection state cleaned", k);
                }
            }
        }
    }

    private async Task ProcessPerpProtectionAsync(
        string ownerPrincipalId, string exchange, string symbol, string side, decimal qty,
        decimal entryPrice, decimal markPrice, decimal liqPrice, decimal liqDistPct, int leverage,
        CancellationToken ct)
    {
        var key = $"{ownerPrincipalId}:{exchange}:{symbol}:{side}";
        var now = DateTime.UtcNow;
        var isLong = side == "long";

        // 沒 state（新部位）才需要 fetch ATR——既有 state 不重算 SL；reset 時用 cache hit 不會重打 quote
        decimal effectiveSlPct;
        if (_perpPositionState.ContainsKey(key))
            effectiveSlPct = _protectionConfig.InitialSlPct;  // 不會用到、僅為 lambda capture
        else
        {
            // 槓桿感知：軟 SL 也要收緊到強平距離以內，否則高槓桿時 SL 掛在強平價之後、broker 先看到也來不及
            // （全倉模式 _disableLevSlCap=true → 不收緊、抱反彈）
            var fixedBase = await ComputeEffectiveSlPctAsync(exchange, symbol, entryPrice, ct);
            // 2026-06-04:UseSignalSl 腿開倉時若有橋接到 signal stop(諧波 PRZ 失效價)→ soft SL 也用它,
            // 跟 bracket SL 一致(否則 soft 停在固定%、會在 PRZ 失效前提前平倉、白費結構性停損)。
            var baseSlPct = _signalSlByKey.TryGetValue(key, out var sigSl)
                ? ResolveBaseSlPct(entryPrice, isLong, useSignalSl: true, signalStop: sigSl, fixedFallbackPct: fixedBase)
                : fixedBase;
            effectiveSlPct = LeverageAwareSlPct(baseSlPct, leverage, _disableLevSlCap);
        }

        var state = _perpPositionState.GetOrAdd(key, _ =>
        {
            var initialSl = isLong
                ? entryPrice * (1m - effectiveSlPct / 100m)
                : entryPrice * (1m + effectiveSlPct / 100m);
            var slMode = _protectionConfig.AtrSlMultiplier > 0
                && Math.Abs(effectiveSlPct - _protectionConfig.InitialSlPct) > 0.01m
                ? $"ATR×{_protectionConfig.AtrSlMultiplier}" : "fixed";
            _logger.LogInformation(
                "Perp protection state init for {Key}: entry={Entry:F4}, SL={Sl:F4} ({Pct:F2}% {Dir} entry, lev {Lev}x, mode={Mode})",
                key, entryPrice, initialSl, effectiveSlPct, isLong ? "below" : "above", leverage, slMode);
            return new PerpetualPositionState
            {
                OwnerPrincipalId = ownerPrincipalId,
                Exchange = exchange, Symbol = symbol, Side = side,
                EntryPrice = entryPrice, PeakMark = markPrice, SlPrice = initialSl,
                LiquidationPrice = liqPrice, Leverage = leverage,
                PartialExited = false, BeMoved = false,
                CreatedAt = now, UpdatedAt = now,
            };
        });

        // entry 變了（加倉）→ reset state、重用同 effectiveSlPct（短期內 ATR 不會大變）
        if (state.EntryPrice != entryPrice && entryPrice > 0m)
        {
            // 加倉路徑要重新算（因為新 entry、ATR cache 還在）
            if (_protectionConfig.AtrSlMultiplier > 0)
                effectiveSlPct = LeverageAwareSlPct(
                    await ComputeEffectiveSlPctAsync(exchange, symbol, entryPrice, ct), leverage, _disableLevSlCap);
            _logger.LogInformation("Perp entry price changed for {Key}: {Old:F4} → {New:F4} — recomputing SL ({Pct:F2}%)", key, state.EntryPrice, entryPrice, effectiveSlPct);
            state.EntryPrice = entryPrice;
            state.SlPrice = isLong
                ? entryPrice * (1m - effectiveSlPct / 100m)
                : entryPrice * (1m + effectiveSlPct / 100m);
            state.PartialExited = false; state.BeMoved = false; state.PeakMark = markPrice;
        }

        // PeakMark：long 取最高、short 取最低
        if (isLong && markPrice > state.PeakMark) state.PeakMark = markPrice;
        else if (!isLong && (state.PeakMark == 0m || markPrice < state.PeakMark)) state.PeakMark = markPrice;
        state.LiquidationPrice = liqPrice;
        state.UpdatedAt = now;

        // ── Liquidation warning push（比 emergency 早一階、不平倉、給人工 add margin 機會）─
        if (_liqAlertPct > 0m && liqDistPct > 0m && liqDistPct < _liqAlertPct
            && liqDistPct > _perpLiqEmergencyPct)  // 已進 emergency 範圍由下面 case 處理、不再 push
        {
            var alertKey = $"{exchange}:{symbol}:{side}";
            var now2 = DateTime.UtcNow;
            var lastAlert = _lastLiqAlertAt.TryGetValue(alertKey, out var la) ? la : DateTime.MinValue;
            if (now2 - lastAlert > _liqAlertCooldown)
            {
                _lastLiqAlertAt[alertKey] = now2;
                var title = $"⚠ 強平距離警告 · {symbol} {side.ToUpper()}";
                var body = $"距離強平僅 **{liqDistPct:F2}%**（threshold {_liqAlertPct}%）\n" +
                           $"Mark: {markPrice:F4}  Liq: {state.LiquidationPrice:F4}\n" +
                           $"建議手動加保證金或減倉。Emergency 平倉門檻：{_perpLiqEmergencyPct}%";
                try { var d = DiscordNotify; if (d != null) await d.SendAdHocAsync(title, body, color: 0xF6465D, ct); } catch { }
                try { var l = LineNotify; if (l != null) await l.SendAdHocAsync(title, body, level: "warning", ct); } catch { }
                AddLog(_watchList.TryGetValue($"{exchange}:{symbol}", out var wAlert) ? wAlert : new WatchItem { Symbol = symbol, Exchange = exchange },
                    "warn", $"liq alert pushed: distance {liqDistPct:F2}% < {_liqAlertPct}%");
            }
        }

        var decision = ProtectionDecisionEngine.EvaluatePerpetualProtection(state, markPrice, qty, liqDistPct, _protectionConfig, _perpLiqEmergencyPct);
        switch (decision.Action)
        {
            case PerpProtectionAction.LiquidationEmergency:
            case PerpProtectionAction.SlHit:
                await ExecutePerpProtectionOrderAsync(ownerPrincipalId, exchange, symbol, side, qty, decision.Reason, ct);
                if (decision.Action == PerpProtectionAction.SlHit)
                    RecordSlHit(exchange, symbol, now);  // 連環 SL flush 仍以 (exchange, symbol) 算、跨 user 共用
                break;

            case PerpProtectionAction.PartialExit:
            {
                var ok = await ExecutePerpProtectionOrderAsync(ownerPrincipalId, exchange, symbol, side, decision.PartialQty, decision.Reason, ct);
                if (ok) state.PartialExited = true;
                break;
            }

            case PerpProtectionAction.BeMove:
                _logger.LogInformation("Perp BE SL move for {Key}: {Reason}", key, decision.Reason);
                state.SlPrice = decision.NewSlPrice;
                state.BeMoved = true;
                if (_watchList.TryGetValue($"{exchange}:{symbol}", out var wi))
                    AddLog(wi, "protect", decision.Reason);
                await SyncExchangeStopAsync(ownerPrincipalId, exchange, symbol, side, qty, decision.NewSlPrice, "BE", ct);
                break;

            case PerpProtectionAction.TrailingLock:
                _logger.LogInformation("Perp trailing lock for {Key}: {Reason}", key, decision.Reason);
                state.SlPrice = decision.NewSlPrice;  // EvaluatePerpetualProtection 已保證單向移動
                if (_watchList.TryGetValue($"{exchange}:{symbol}", out var wiTrail))
                    AddLog(wiTrail, "protect", decision.Reason);
                await SyncExchangeStopAsync(ownerPrincipalId, exchange, symbol, side, qty, decision.NewSlPrice, "trailing", ct);
                break;

            case PerpProtectionAction.None:
            default:
                break;
        }

        // 不論 action 為何、把 PeakMark / SlPrice / PartialExited / BeMoved 的最新狀態落 DB——
        // broker 重啟後 cycle 才能用既有 state 恢復、避免「entry 用最新 mark 重算 SL 跑遠」。
        PersistPerpState(key, state);
    }

    /// <summary>
    /// 純函式:給定所有輸入（含時間桶）→ deterministic 冪等 key。**無 env、無 UtcNow → 可單元測試**。
    /// 同一意圖（owner|exchange|symbol|side|tag + bucket）→ 同 key → failover 重送 → BingX 擋重複（code=101400）。
    /// ≤36 字元（BingX clientOrderID 上限:prefix(2-4) + '-' + 16 hex）。
    /// （解耦合:把「核心邏輯」從「外部相依 env/時間」分離出來,後者留在 BuildIdemKey 薄包裝。）
    /// </summary>
    public static string DeriveIdemKey(string prefix, string owner, string exchange, string symbol, string side, string tag, long bucket)
    {
        var basis = $"{owner}|{exchange}|{symbol}|{side}|{tag}|{bucket}";
        var h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(basis)));
        return $"{prefix}-{h.Substring(0, 16).ToLowerInvariant()}";
    }

    /// <summary>
    /// finding B — 把平倉 reason 內的浮動數字（價格/pnl%、每輪都變）抽成 # 只留語意類別
    /// （"SL hit"/"Partial exit"/...），得到「同一平倉意圖 → 同字串」的穩定冪等 tag。
    /// 否則 failover 重送算出不同 key → BingX 不去重 → 重複平倉。純函式、好測
    /// （解耦合:把 idempotency tag 規則從 I/O 路徑抽離出來、可單獨驗證）。
    /// </summary>
    public static string StableCloseTag(string reason)
        => System.Text.RegularExpressions.Regex.Replace(reason ?? string.Empty, @"[-+]?\d[\d.,%]*", "#");

    /// <summary>finding A — perp 開倉「需不需要 risk-worker / 能不能放行」的三種模式。</summary>
    public enum PerpOpenRiskMode
    {
        RunRiskWorker,        // risk-worker 在線 → 跑 r14/r16 pre_perp_order
        ProceedWithBracketSl, // 離線 + bracket SL 已啟用 → 放行(交易所端 SL 近似涵蓋 r14 per-trade 損失)
        FailClosedBlock,      // 離線 + bracket SL 未啟用 → 無 r14/r16 又無交易所止損 = 真裸倉 → 擋下
    }

    /// <summary>
    /// finding A — perp 開倉風控模式的純決策（解耦合、好測:無 I/O、無 env）。broker-local 閘
    /// (max倉/相關性/funding/sizing/pre-flight)在三種模式下都已先執行（在此函式之外、一律跑）。
    /// 這裡只決定「r14/r16 那段」怎麼處理:在線就跑;離線時靠 bracket SL 撐 → 有開就放行、沒開就 fail-closed。
    /// </summary>
    public static PerpOpenRiskMode ResolvePerpOpenRiskMode(bool riskWorkerAvailable, bool bracketSlEnabled)
        => riskWorkerAvailable ? PerpOpenRiskMode.RunRiskWorker
         : bracketSlEnabled    ? PerpOpenRiskMode.ProceedWithBracketSl
         :                       PerpOpenRiskMode.FailClosedBlock;

    /// <summary>
    /// finding C — 當日 PnL%（純算術、好測）。open&lt;=0(新帳戶/查不到)→ 0、不誤觸熔斷(沿用除零防呆)。
    /// 純函式只算「%」,「該餵 equity 還是 balance」由呼叫端決定 —— 餵 equity 才能反映未實現浮虧(finding C 根因層)。
    /// </summary>
    public static decimal ComputeDayPnlPct(decimal current, decimal open)
        => open <= 0m ? 0m : (current - open) / open * 100m;

    /// <summary>
    /// finding D — portfolio 熔斷的 scope key。spot 用 exchange、perp 用 owner:exchange(多用戶隔離,
    /// 否則同 exchange 不同用戶的 equity peak 互相污染)。owner 空 → prn_dashboard。純函式、好測。
    /// </summary>
    public static string BuildCbScopeKey(string? ownerPrincipalId, string exchange)
        => $"{(string.IsNullOrEmpty(ownerPrincipalId) ? "prn_dashboard" : ownerPrincipalId)}:{exchange}";

    /// <summary>
    /// finding J — scale-in 門檻的 existing 取 max(部位筆數, broker 累計加碼次數)。hedge 模式部位合併成 1 筆
    /// → 筆數恆 ≤1、門檻凍結;改用累計次數讓門檻隨加碼深度累進。取 max 是 fail-safe:broker 重啟丟計數時
    /// 至少還原成「有倉 → 至少 1」的舊行為(偏保守不偏放鬆)。純函式、好測。
    /// </summary>
    public static int ResolveScaleInExisting(int positionRowCount, int recordedScaleInCount)
        => Math.Max(positionRowCount, recordedScaleInCount);

    /// <summary>
    /// 薄包裝:處理「外部相依」（env flag gate + 當前 5 分鐘時間桶）。
    /// **預設啟用**（finding K）:BingX clientOrderID 去重已 live 驗證（code=101400）、且 spot 路徑本就一律開,
    /// perp 不開 = failover 重送會雙開倉/重複平倉的不對稱缺口。緊急可設 AUTOTRADER_IDEMPOTENT_KEYS=0
    /// 退回隨機 key。5 分鐘桶覆蓋 failover 窗口、桶換了=新意圖。
    /// </summary>
    private static string? BuildIdemKey(string prefix, string owner, string exchange, string symbol, string side, string tag)
    {
        var flag = Environment.GetEnvironmentVariable("AUTOTRADER_IDEMPOTENT_KEYS");
        if (flag is "0" or "false" or "False" or "FALSE" or "off" or "OFF") return null; // 緊急關閉開關
        var bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300;
        return DeriveIdemKey(prefix, owner, exchange, symbol, side, tag, bucket);
    }

    /// <summary>
    /// 平倉用：long 倉用 sell + position_side=long + reduce_only；short 倉用 buy + position_side=short + reduce_only。
    /// </summary>
    private async Task<bool> ExecutePerpProtectionOrderAsync(
        string ownerPrincipalId, string exchange, string symbol, string side, decimal qty, string reason, CancellationToken ct)
    {
        // 平多 = sell + LONG + reduceOnly；平空 = buy + SHORT + reduceOnly
        var orderSide = side == "long" ? "sell" : "buy";
        var watchKey = $"{exchange}:{symbol}";
        // 帶 user 自己的 credential、平倉才會打到 user 自己的帳戶（沒設則 fallback env 預設）
        var creds = BuildCredentialsObject(ownerPrincipalId, exchange);
        // 平倉也把該 watch 的 strategy 帶上、SaveTrade 才能把這筆 close trade 歸到原策略
        var watchStrategy = _watchList.TryGetValue(watchKey, out var wWatch) ? wWatch.Strategy : null;
        // 冪等 key:同一平倉意圖 → 同 key → failover 重送被 BingX 擋（code=101400）。
        // finding B 修:reason 含浮動價/pnl% → 用 StableCloseTag 抽掉數字、只留語意類別當穩定 tag。
        var idemKey = BuildIdemKey("cl", ownerPrincipalId, exchange, symbol, side, StableCloseTag(reason));
        var orderPayload = creds == null
            ? JsonSerializer.Serialize(new
            {
                exchange, symbol, side = orderSide, position_side = side,
                order_type = "market", quantity = qty, reduce_only = true,
                strategy = watchStrategy, client_order_id = idemKey,
            })
            : JsonSerializer.Serialize(new
            {
                exchange, symbol, side = orderSide, position_side = side,
                order_type = "market", quantity = qty, reduce_only = true,
                strategy = watchStrategy, client_order_id = idemKey,
                __credentials = creds,
            });
        var result = await _dispatcher.DispatchAsync(BuildRequest("trading.perpetual", "place_order", orderPayload));
        if (result.Success)
        {
            // finding E:BingX 回 idempotent_duplicate（code=101400）= 此平倉意圖先前已送出、本次未產生新動作。
            // 之前 broker 一律當成「剛成功平倉」記錄 → 重送會被誤記成第二次真實平倉。分流記錄(audit 可見)、不灌假動作。
            var wasDuplicate = result.ResultPayload?.Contains("idempotent_duplicate", StringComparison.OrdinalIgnoreCase) == true;
            if (wasDuplicate)
            {
                _logger.LogWarning("🛡 Perp protection close — BingX idempotent_duplicate(此意圖先前已送出、無新動作):{Symbol} {Side} {Qty} on {Exchange} — {Reason}",
                    symbol, side, qty, exchange, reason);
                if (_watchList.TryGetValue(watchKey, out var wiDup))
                    AddLog(wiDup, "protect", $"perp close {side.ToUpper()} {qty} (idempotent dup — 已由先前送出處理) — {reason}");
            }
            else
            {
                _logger.LogInformation("🛡 Perp protection close: {Symbol} {Side}({OrdSide}) {Qty} on {Exchange} — {Reason}",
                    symbol, side, orderSide, qty, exchange, reason);
                if (_watchList.TryGetValue(watchKey, out var wi))
                    AddLog(wi, "protect", $"perp close {side.ToUpper()} {qty} — {reason}");
            }
            return true;
        }
        _logger.LogWarning("Perp protection close failed: {Symbol} {Side} {Qty} on {Exchange} — {Error}",
            symbol, side, qty, exchange, result.ErrorMessage);
        if (_watchList.TryGetValue(watchKey, out var wi2))
            AddLog(wi2, "error", $"perp protection close failed: {result.ErrorMessage}");
        return false;
    }

    /// <summary>
    /// #1 — broker 移動軟 SL（BE/trailing）時，把 exchange 端的 stop 也 cancel+replace 到新價，
    /// 讓 downtime 期間也鎖在已實現的利潤、而非開倉 entry−SL。預設 off（_bracketSlSyncEnabled）。
    /// 失敗只 log + AddLog warn、不擋 protection 主流程（broker 軟 SL 仍生效）。
    /// </summary>
    private async Task SyncExchangeStopAsync(
        string ownerPrincipalId, string exchange, string symbol, string side, decimal qty, decimal newSlPrice, string label, CancellationToken ct)
    {
        if (!_bracketSlSyncEnabled) return;
        await DispatchSetPositionSlAsync(ownerPrincipalId, exchange, symbol, side, qty, newSlPrice, label, ct);
    }

    /// <summary>
    /// 真正打 set_position_sl 的共用實作（被 #1 sync 跟 #2 retrofit 共用）。flag 檢查由 caller 負責。
    /// 失敗只 log + AddLog warn、不擋 protection 主流程（broker 軟 SL 仍生效）。
    /// </summary>
    private async Task DispatchSetPositionSlAsync(
        string ownerPrincipalId, string exchange, string symbol, string side, decimal qty, decimal newSlPrice, string label, CancellationToken ct)
    {
        if (qty <= 0m || newSlPrice <= 0m) return;
        var watchKey = $"{exchange}:{symbol}";
        // 平多用 SELL+LONG、平空用 BUY+SHORT，跟平倉同一套方向規則
        var closeSide = side == "long" ? "sell" : "buy";
        var pricePrecision = BrokerCore.Trading.SymbolSpecs.GetSpec(exchange, symbol)?.PricePrecision;
        var creds = BuildCredentialsObject(ownerPrincipalId, exchange);
        var payloadObj = new Dictionary<string, object?>
        {
            ["exchange"]       = exchange,
            ["symbol"]         = symbol,
            ["position_side"]  = side,
            ["close_side"]     = closeSide,
            ["quantity"]       = qty,
            ["stop_loss_price"] = RoundPrice(newSlPrice, pricePrecision),
        };
        if (creds != null) payloadObj["__credentials"] = creds;
        var result = await _dispatcher.DispatchAsync(
            BuildRequest("trading.perpetual", "set_position_sl", JsonSerializer.Serialize(payloadObj)));
        if (result.Success)
        {
            _logger.LogInformation("🛡 Exchange SL set ({Label}) {Symbol} {Side} → {Sl:F4}", label, symbol, side, newSlPrice);
            if (_watchList.TryGetValue(watchKey, out var wi))
                AddLog(wi, "protect", $"exchange SL set ({label}) → {newSlPrice:F4}");
        }
        else
        {
            _logger.LogWarning("Exchange SL set failed ({Label}) {Symbol} {Side} → {Sl:F4}: {Error}",
                label, symbol, side, newSlPrice, result.ErrorMessage);
            if (_watchList.TryGetValue(watchKey, out var wi))
                AddLog(wi, "warn", $"exchange SL set failed ({label}): {result.ErrorMessage} (軟 SL 仍生效)");
        }
    }

    /// <summary>
    /// #2 — 裸倉自動補 SL：若某倉交易所端「沒掛任何 STOP」，補一張到 broker 當前軟 SL 價。
    /// 預設 off（_retrofitNakedSl）。每 cycle 查 open orders 判斷裸倉、補完下次就不再補（已有 STOP）。
    /// </summary>
    private async Task RetrofitNakedStopAsync(
        string ownerPrincipalId, string exchange, string symbol, string side, decimal qty, decimal slPrice, CancellationToken ct)
    {
        if (!_retrofitNakedSl || qty <= 0m || slPrice <= 0m) return;
        var hasStop = await HasExchangeStopAsync(ownerPrincipalId, exchange, symbol, side, ct);
        if (hasStop == null) return;          // 查 open orders 失敗 → 不確定、保守不補（避免重複下）
        if (hasStop == true) return;          // 已有 STOP → 不是裸倉、跳過
        _logger.LogInformation("🩹 Naked perp position detected {Exchange}:{Symbol} {Side} — retrofitting SL @ {Sl:F4}", exchange, symbol, side, slPrice);
        await DispatchSetPositionSlAsync(ownerPrincipalId, exchange, symbol, side, qty, slPrice, "retrofit", ct);
    }

    /// <summary>
    /// 查某 (exchange, symbol, side) 在交易所端有沒有 STOP 類 reduce 單。
    /// 回 null = 查詢失敗（不確定）；true/false = 有/沒有。給 #2 retrofit 判斷裸倉用。
    /// </summary>
    private async Task<bool?> HasExchangeStopAsync(
        string ownerPrincipalId, string exchange, string symbol, string side, CancellationToken ct)
    {
        var creds = BuildCredentialsObject(ownerPrincipalId, exchange);
        var payloadObj = new Dictionary<string, object?> { ["exchange"] = exchange, ["symbol"] = symbol };
        if (creds != null) payloadObj["__credentials"] = creds;
        var result = await _dispatcher.DispatchAsync(
            BuildRequest("trading.perpetual", "get_open_orders", JsonSerializer.Serialize(payloadObj)));
        if (!result.Success) return null;
        try
        {
            var doc = JsonDocument.Parse(result.ResultPayload ?? "{}").RootElement;
            if (!doc.TryGetProperty("orders", out var arr) || arr.ValueKind != JsonValueKind.Array) return false;
            foreach (var o in arr.EnumerateArray())
            {
                var type = o.TryGetProperty("order_type", out var t) ? t.GetString() ?? "" : "";
                var posSide = o.TryGetProperty("position_side", out var ps) ? ps.GetString() ?? "" : "";
                if (type.Contains("stop", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(posSide, side, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch { return null; }
    }

    private async Task ProcessSymbolAsync(WatchItem item, CancellationToken ct)
    {
        var symbol   = item.Symbol;
        var exchange = item.Exchange;

        // Step 1: 拉 K 線
        if (!_registry.HasAvailableWorker("quote.ohlcv"))
        {
            AddLog(item, "skip", "quote-worker not connected");
            return;
        }

        // limit 200(原 100):ts_momentum 等策略需 ≥114 根,抓 100 會永遠「資料不足」→ 該腿廢掉。
        var barsPayload = JsonSerializer.Serialize(new { symbol, interval = "1d", limit = 200 });
        var barsResult = await _dispatcher.DispatchAsync(BuildRequest("quote.ohlcv", "get_bars", barsPayload));
        if (!barsResult.Success)
        {
            AddLog(item, "skip", $"Failed to get bars: {barsResult.ErrorMessage}");
            return;
        }

        var barsDoc = JsonDocument.Parse(barsResult.ResultPayload ?? "{}");
        if (!barsDoc.RootElement.TryGetProperty("bars", out var barsArr) || barsArr.GetArrayLength() < 30)
        {
            AddLog(item, "skip", $"Not enough bars: {barsArr.GetArrayLength()}");
            return;
        }

        // Step 2: 策略分析
        if (!_registry.HasAvailableWorker("strategy.signal"))
        {
            AddLog(item, "skip", "strategy-worker not connected");
            return;
        }

        // Batch C+++ Phase 2：若 watch entry 有 htf_interval、額外 fetch HTF bars 一併傳給策略
        // strategy-worker 端 HarmonicStrategy 會用 HtfBars 做大週期方向確認
        var payloadDict = new Dictionary<string, object?>
        {
            ["strategy"] = item.Strategy,
            ["symbol"]   = symbol,
            ["exchange"] = exchange,
            ["interval"] = "1d",
            ["bars"]     = barsArr,
        };
        if (!string.IsNullOrEmpty(item.HtfInterval))
        {
            var htfBarsPayload = JsonSerializer.Serialize(new { symbol, interval = item.HtfInterval, limit = 200 });
            var htfResult = await _dispatcher.DispatchAsync(BuildRequest("quote.ohlcv", "get_bars", htfBarsPayload));
            if (htfResult.Success)
            {
                var htfDoc = JsonDocument.Parse(htfResult.ResultPayload ?? "{}");
                if (htfDoc.RootElement.TryGetProperty("bars", out var htfBarsArr) &&
                    htfBarsArr.GetArrayLength() >= 30)
                {
                    payloadDict["htf_interval"] = item.HtfInterval;
                    payloadDict["htf_bars"]     = htfBarsArr;
                }
                else
                {
                    AddLog(item, "warn", $"HTF {item.HtfInterval} bars insufficient ({htfBarsArr.GetArrayLength()}); 退化成單週期");
                }
            }
            else
            {
                AddLog(item, "warn", $"HTF {item.HtfInterval} fetch failed: {htfResult.ErrorMessage}; 退化成單週期");
            }
        }

        var signalPayload = JsonSerializer.Serialize(payloadDict);
        var signalResult = await _dispatcher.DispatchAsync(BuildRequest("strategy.signal", "evaluate", signalPayload));
        if (!signalResult.Success)
        {
            AddLog(item, "skip", $"Strategy failed: {signalResult.ErrorMessage}");
            return;
        }

        var signal = JsonDocument.Parse(signalResult.ResultPayload ?? "{}").RootElement;
        var action = signal.TryGetProperty("action", out var a) ? a.GetString() ?? "hold" : "hold";
        var confidence = signal.TryGetProperty("confidence", out var c) ? c.GetDecimal() : 0;
        var reason = signal.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
        // 2026-06-04:UseSignalSl 腿解析 signal 的 stop_loss(諧波 PRZ 失效價)→ 暫存到 item,傳給開倉路徑當結構性 SL。
        item.PendingSignalSl = null;
        if (item.UseSignalSl && signal.TryGetProperty("stop_loss", out var slEl)
            && slEl.ValueKind == JsonValueKind.Number)
        {
            var sl = slEl.GetDecimal();
            if (sl > 0m) item.PendingSignalSl = sl;
        }
        // strategy-worker 從 0a48xx 版起 evaluate response 多帶 regime side-channel；舊 worker 就是空字串
        var regimeType = signal.TryGetProperty("regime", out var regEl) && regEl.ValueKind == JsonValueKind.Object
            && regEl.TryGetProperty("type", out var rt) ? rt.GetString() ?? "" : "";

        item.LastSignal = action;
        item.LastConfidence = confidence;
        item.LastCheck = DateTime.UtcNow;
        PersistWatch($"{exchange}:{symbol}", item, onlyLastCheck: true);

        // Dev-only override：env 設了就覆蓋訊號 + 跳過 threshold（用於 e2e 測試）
        if (_devForceAction != null)
        {
            _logger.LogWarning("⚠ AUTOTRADER_DEV_FORCE_ACTION overriding {Original}@{Conf:P0} → {Forced} for {Symbol}",
                action, confidence, _devForceAction, symbol);
            action = _devForceAction;
            AddLog(item, "force", $"DEV: forcing {action} (real signal was {item.LastSignal}@{confidence:P0})");
        }
        else
        {
            if (action == "hold")
            {
                AddLog(item, "hold", $"Confidence={confidence:P0}. {TruncateReason(reason)}");
                return;
            }

            // 信心度門檻（env AUTOTRADER_MIN_CONFIDENCE 可調，預設 0.5）
            if (confidence < _minConfidence)
            {
                AddLog(item, "skip", $"Signal={action} but confidence {confidence:P0} < {_minConfidence:P0} threshold");
                return;
            }
        }

        // Step 3: 取得價格估算
        decimal price = 0;
        if (signal.TryGetProperty("indicators", out var indicators))
        {
            foreach (var prop in indicators.EnumerateObject())
            {
                if (prop.Name.EndsWith(".price") || prop.Name == "price")
                {
                    price = prop.Value.GetDecimal();
                    break;
                }
            }
        }

        // Step 4: 風控檢查
        // perp watch 整段跳過 spot risk——spot 路徑用「USDT 名目」算 max_position，
        // 套到 perp 的 base-unit qty (e.g., 0.08 SOL) 會把 qty 誤調大成 ~108 SOL，
        // 然後 perp risk gate 才看到 $10k 名目把它擋下、形成 false-blocked log。
        // perp watch 的真正風控走 PlacePerpOrderForSignalAsync 內部的 pre_perp_order 路徑、
        // 那邊有 r11-r15 完整 perp 規則 + circuit breaker 也已經在 perp 路徑各自處理。
        var isPerpMode = item.Mode == "perp_long_only" || item.Mode == "perp_both";
        if (!isPerpMode && _registry.HasAvailableWorker("risk.check") && price > 0)
        {
            var riskPayload = JsonSerializer.Serialize(new
            {
                symbol, exchange, side = action,
                quantity = item.Quantity, price,
                portfolio = new { cash = 0, portfolio_value = 0, day_pnl = 0, peak_value = 0, daily_trade_count = 0, positions = Array.Empty<object>() }
            });

            // 嘗試先取得真正的帳戶資訊
            if (_registry.HasAvailableWorker("trading.account"))
            {
                var accResult = await _dispatcher.DispatchAsync(BuildRequest("trading.account", "get_account",
                    JsonSerializer.Serialize(new { exchange })));
                var posResult = await _dispatcher.DispatchAsync(BuildRequest("trading.account", "get_positions",
                    JsonSerializer.Serialize(new { exchange })));
                // Fill poller 已在維護 trades 表，這裡查當天累計筆數餵給 risk engine 的
                // max_daily_trades 規則（之前永遠傳 0、規則形同虛設）
                var dailyResult = await _dispatcher.DispatchAsync(BuildRequest("trading.account", "daily_trade_count",
                    JsonSerializer.Serialize(new { exchange })));
                var dailyCount = 0;
                if (dailyResult.Success)
                {
                    var dc = JsonDocument.Parse(dailyResult.ResultPayload ?? "{}").RootElement;
                    if (dc.TryGetProperty("count", out var cnt)) dailyCount = cnt.GetInt32();
                }

                // 每個 (exchange:symbol) 最近交易時間 → cooldown_seconds 規則用，防 signal 抖動連續開單
                var lastTradesResult = await _dispatcher.DispatchAsync(BuildRequest("trading.account", "last_trade_times", "{}"));
                var lastTradesJson = lastTradesResult.Success
                    ? JsonDocument.Parse(lastTradesResult.ResultPayload ?? "{}").RootElement
                    : JsonDocument.Parse("{}").RootElement;

                if (accResult.Success && posResult.Success)
                {
                    var acc = JsonDocument.Parse(accResult.ResultPayload ?? "{}").RootElement;
                    var pos = JsonDocument.Parse(posResult.ResultPayload ?? "{}").RootElement;

                    // ── Portfolio circuit breaker（B1）──
                    // 在 risk-engine 之前先查當日 DD，超過 _maxPortfolioDdPct 就直接擋、
                    // 不浪費 risk-engine 的算力、log 也明確標 halt 而非 risk reject。
                    var portfolioValue = acc.TryGetProperty("portfolio_value", out var pvCb) ? pvCb.GetDecimal() : 0m;
                    var cb = EvaluateCircuitBreaker(exchange, portfolioValue, DateTime.UtcNow);
                    if (cb.Triggered)
                    {
                        _logger.LogWarning(
                            "⚠ Circuit breaker triggered on {Exchange}: DD {Dd}% ≥ {Threshold}% (peak={Peak:C}, current={Cur:C}). Skipping {Symbol} {Action}.",
                            exchange, cb.DdPct, cb.Threshold, cb.PeakValue, cb.CurrentValue, symbol, action);
                        AddLog(item, "halt",
                            $"⚠ Portfolio DD {cb.DdPct:F1}% ≥ {cb.Threshold}% on {exchange} (peak={cb.PeakValue:F2}, now={cb.CurrentValue:F2}) — order blocked");
                        // 2026-05-27 真錢安全強化:首次觸發 / DD 惡化 +2pp 推 Discord critical alert
                        // spot:scopeKey 與 displayLabel 都用 exchange(單帳戶、無 per-user 需求)
                        _ = TryPushCbAlertAsync(exchange, exchange, cb, ct);
                        return;
                    }

                    var lastTradesDict = new Dictionary<string, DateTime>();
                    if (lastTradesJson.TryGetProperty("last_trades", out var lt) && lt.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in lt.EnumerateObject())
                            if (prop.Value.ValueKind == JsonValueKind.String && DateTime.TryParse(prop.Value.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                                lastTradesDict[prop.Name] = dt;
                    }

                    riskPayload = JsonSerializer.Serialize(new
                    {
                        symbol, exchange, side = action,
                        quantity = item.Quantity, price,
                        // 給 risk.check 的 max_loss_per_trade_pct rule 用、protection_config 共用同一個 SL
                        initial_sl_pct = _protectionConfig.InitialSlPct,
                        portfolio = new
                        {
                            cash = acc.TryGetProperty("cash", out var cash) ? cash.GetDecimal() : 0,
                            portfolio_value = acc.TryGetProperty("portfolio_value", out var pv) ? pv.GetDecimal() : 0,
                            day_pnl = acc.TryGetProperty("day_pnl", out var dp) ? dp.GetDecimal() : 0,
                            peak_value = acc.TryGetProperty("portfolio_value", out var pk) ? pk.GetDecimal() : 0,
                            daily_trade_count = dailyCount,
                            positions = pos.TryGetProperty("positions", out var posArr2) ? posArr2 : JsonDocument.Parse("[]").RootElement,
                            last_trade_by_symbol = lastTradesDict,
                        }
                    });
                }
            }

            var riskResult = await _dispatcher.DispatchAsync(BuildRequest("risk.check", "pre_order", riskPayload));
            if (riskResult.Success)
            {
                var riskDoc = JsonDocument.Parse(riskResult.ResultPayload ?? "{}").RootElement;
                var passed = riskDoc.TryGetProperty("passed", out var p) && p.GetBoolean();
                var orderAction = riskDoc.TryGetProperty("order_action", out var oa) ? oa.GetString() : "reject";

                if (!passed && orderAction == "reject")
                {
                    // 比照 perp 路徑:把 risk-worker 回的「具體違反規則」寫進 log/RecentLogs。
                    // 原本只記通用 "Risk check rejected order"、無法從觀測面 debug 是哪條規則(r17/cash/cooldown…)擋的。
                    var msgs = new List<string>();
                    if (riskDoc.TryGetProperty("violations", out var vs) && vs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in vs.EnumerateArray())
                            if (v.TryGetProperty("message", out var vm)) msgs.Add(vm.GetString() ?? "");
                    }
                    var summary = msgs.Count > 0 ? string.Join("; ", msgs) : "risk rejected";
                    AddLog(item, "blocked", $"Risk check rejected: {summary}");
                    _logger.LogWarning("AutoTrader risk blocked {Symbol} on {Exchange}: {Msg}", symbol, exchange, summary);
                    return;
                }

                if (orderAction == "reduce" && riskDoc.TryGetProperty("adjusted_qty", out var aq))
                {
                    item.Quantity = aq.GetDecimal();
                    PersistWatch($"{exchange}:{symbol}", item);
                    AddLog(item, "adjusted", $"Risk reduced qty to {item.Quantity}");
                }
            }
        }

        // Shadow（影子）守衛：訊號照評估、風控照跑、log 照記,但「絕不下任何真單」。
        // 攔在下單分歧之前——perp / spot 兩條路都吃得到。新策略(SMC 日線)上線前
        // 先 shadow 跑幾週對帳「實盤訊號 vs 回測」,確認後才拿掉 shadow 旗標放真錢。
        if (item.Shadow)
        {
            AddLog(item, "shadow", $"[SHADOW] 本來會下單:{action} · {item.Mode} · conf={confidence:P0} — 已記錄、未下單");
            return;
        }

        // Step 5: 下單 — spot 走 trading.order；perp_* 走 trading.perpetual 並做 signal→open/close 映射
        if (item.Mode == "perp_long_only" || item.Mode == "perp_both")
        {
            await PlacePerpOrderForSignalAsync(item, action, confidence, regimeType, ct);
        }
        else
        {
            await PlaceSpotOrderAsync(item, action, exchange, symbol, ct);
        }
    }

    /// <summary>既有 spot 下單路徑（拆出來讓 perp 分支不亂）。</summary>
    private async Task PlaceSpotOrderAsync(WatchItem item, string action, string exchange, string symbol, CancellationToken ct)
    {
        if (!_registry.HasAvailableWorker("trading.order"))
        {
            AddLog(item, "skip", "trading-worker not connected");
            return;
        }

        // Deterministic client_order_id：同一 5 分鐘 bucket 內 retry 同 (exchange/symbol/side/qty)
        // 都會收到同一個 ID，trading-worker 端 + 交易所端各有一道 dedup
        var bucket5min = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300;
        var clientOrderId = BuildAutoOrderKey(exchange, symbol, action, item.Quantity, bucket5min);

        var orderPayload = JsonSerializer.Serialize(new
        {
            exchange, symbol, side = action,
            quantity = item.Quantity, order_type = "market",
            client_order_id = clientOrderId,
            strategy = item.Strategy,   // 標策略 → spot 成交 trade 才能 per-strategy 歸屬(forward P&L)
        });

        var orderResult = await _dispatcher.DispatchAsync(BuildRequest("trading.order", "place_order", orderPayload));
        if (orderResult.Success)
        {
            var order = JsonDocument.Parse(orderResult.ResultPayload ?? "{}").RootElement;
            var orderId = order.TryGetProperty("order_id", out var oid) ? oid.GetString() : "?";
            var status = order.TryGetProperty("status", out var st) ? st.GetString() : "?";
            var isDedup = order.TryGetProperty("idempotent", out var idem) && idem.GetBoolean();
            if (isDedup)
                AddLog(item, "dedup", $"[DEDUP] {orderId} same-bucket retry, no new exchange call (existing status={status})");
            else
            {
                AddLog(item, action, $"ORDER PLACED: {orderId} {action} {item.Quantity} {symbol} @ market → {status}");
                _logger.LogInformation("AutoTrader: {Action} {Qty} {Symbol} on {Exchange} → {Status}",
                    action, item.Quantity, symbol, exchange, status);
            }
        }
        else
        {
            AddLog(item, "error", $"Order failed: {orderResult.ErrorMessage}");
        }
    }

    /// <summary>
    /// Phase 3：perp 分支——把 strategy 的 buy/sell 訊號映射到 open/close + LONG/SHORT。
    ///
    /// 映射規則：先查當前部位、再決定動作
    ///   perp_long_only:
    ///     buy  + 無倉            → open long
    ///     buy  + 已 long          → skip (不加倉)
    ///     sell + has long         → close long
    ///     sell + 無倉             → skip (long_only 不開空)
    ///   perp_both:
    ///     buy  + 無倉            → open long
    ///     buy  + has long         → skip
    ///     buy  + has short        → close short (保守、不立即翻多)
    ///     sell + 無倉             → open short
    ///     sell + has short        → skip
    ///     sell + has long         → close long (保守、不立即翻空)
    ///
    /// 翻倉等下次 cycle 才開、避免一次 cycle 內連續打交易所。
    /// </summary>
    // A2 ATR cache：避免每次 sweep 都打 quote.indicator（4 watches × N sweeps/天 = 太多呼叫）
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (decimal AtrPct, DateTime At)> _atrCache = new();
    private static readonly TimeSpan AtrCacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 計算進場 SL 應該距離 entry 幾 %。
    /// AtrSlMultiplier ≤ 0 → 走 fixed `InitialSlPct`（向後相容）。
    /// AtrSlMultiplier &gt; 0 → 從 quote-worker 拉 ATR(period)、回 multiplier × ATR / entry × 100；
    /// clamp 在 [InitialSlPct × 0.5, InitialSlPct × 3] 防 ATR 異常給出極端值。
    /// 任何失敗 → fallback InitialSlPct（fail-safe，real money 不該因為 quote 沒回就拒開倉）。
    /// </summary>
    private async Task<decimal> ComputeEffectiveSlPctAsync(string exchange, string symbol, decimal entryPrice, CancellationToken ct)
    {
        var cfg = _protectionConfig;
        if (cfg.AtrSlMultiplier <= 0m || entryPrice <= 0m)
            return cfg.InitialSlPct;

        var cacheKey = $"{exchange}:{symbol}:{cfg.AtrInterval}:{cfg.AtrPeriod}";
        var now = DateTime.UtcNow;
        if (_atrCache.TryGetValue(cacheKey, out var cached) && now - cached.At < AtrCacheTtl)
            return ClampAtrPct(cached.AtrPct, cfg.InitialSlPct);

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                symbol,
                interval = cfg.AtrInterval,
                limit = Math.Max(cfg.AtrPeriod * 4, 50),
                period = cfg.AtrPeriod,
            });
            var req = new BrokerCore.Contracts.ApprovedRequest
            {
                RequestId = Guid.NewGuid().ToString("N"),
                CapabilityId = "quote.indicator", Route = "atr", Payload = payload,
                Scope = "{}", PrincipalId = "system",
                TaskId = "auto-trader-atr", SessionId = "auto-trader-atr",
            };
            var result = await _dispatcher.DispatchAsync(req);
            if (!result.Success)
            {
                _logger.LogDebug("ATR fetch failed for {Sym} ({Iv}): {Err}; falling back to fixed",
                    symbol, cfg.AtrInterval, result.ErrorMessage);
                return cfg.InitialSlPct;
            }
            var doc = JsonDocument.Parse(result.ResultPayload ?? "{}").RootElement;
            if (!doc.TryGetProperty("series", out var series) || series.ValueKind != JsonValueKind.Array)
                return cfg.InitialSlPct;
            var arr = series.EnumerateArray().ToList();
            if (arr.Count == 0) return cfg.InitialSlPct;
            // 取最後一筆 ATR 值
            if (!arr[^1].TryGetProperty("value", out var av)) return cfg.InitialSlPct;
            var lastAtr = av.GetDecimal();
            if (lastAtr <= 0m) return cfg.InitialSlPct;

            var atrSlPct = cfg.AtrSlMultiplier * lastAtr / entryPrice * 100m;
            _atrCache[cacheKey] = (atrSlPct, now);
            var clamped = ClampAtrPct(atrSlPct, cfg.InitialSlPct);
            _logger.LogInformation(
                "ATR-based SL for {Sym} ({Iv}, period={P}): ATR={Atr:F4} entry={Ent:F4} mult={M} → raw={Raw:F2}% clamped={Cl:F2}%",
                symbol, cfg.AtrInterval, cfg.AtrPeriod, lastAtr, entryPrice, cfg.AtrSlMultiplier, atrSlPct, clamped);
            return clamped;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ComputeEffectiveSlPctAsync failed for {Sym}, fallback fixed", symbol);
            return cfg.InitialSlPct;
        }
    }

    private static decimal ClampAtrPct(decimal pct, decimal fixedPct)
    {
        var lo = fixedPct * 0.5m;
        var hi = fixedPct * 3m;
        return Math.Max(lo, Math.Min(hi, pct));
    }

    /// <summary>
    /// 取得 / 建立今日 UTC 開盤 equity（給 r16 daily_loss circuit breaker 用），
    /// 計算 (current_equity - today_open_equity) / today_open_equity × 100。
    /// finding C:尺必須用 equity(=balance+未實現浮虧),否則抱浮虧倉時 day_pnl% 算不出真實虧損、r16 永不觸發。
    /// 當前值與當日開盤基準必須同尺(都 equity);PerpDailyOpenBalance.Balance 欄位沿用、語意改成「當日開盤 equity」。
    ///
    /// 邏輯：
    ///   - 若 (exchange, today UTC) 沒紀錄 → 寫一筆 today_open = current_balance、回 0%
    ///   - 已有紀錄 → 用該紀錄當分母算 PnL%
    ///   - today_open ≤ 0 → 回 0（避免除以 0、且新帳戶從沒錢開始本來就不該觸發）
    ///
    /// broker 重啟也保留（持久化到 DB），UTC 跨日下個 cycle 自動寫新紀錄。
    /// 沒有特地重置舊紀錄、舊 row 累積在 DB 中、之後想做歷史趨勢可以用。
    /// </summary>
    private decimal ComputePerpDayPnlPct(string ownerPrincipalId, string exchange, decimal currentEquity)
    {
        try
        {
            // 2026-06-02 多用戶:day-open balance 按 (owner:exchange:date) 隔離。
            // 之前 key=exchange:date → 同交易所先下單者的餘額會變成全交易所當日基準、
            // 其他用戶的 r16 當日虧損熔斷會用「別人的餘額」算 day_pnl%(雙向都錯)。
            var owner = string.IsNullOrEmpty(ownerPrincipalId) ? "prn_dashboard" : ownerPrincipalId;
            var utcDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var key = $"{owner}:{exchange}:{utcDate}";
            var existing = _db.Get<BrokerCore.Models.PerpDailyOpenBalance>(key);
            if (existing == null)
            {
                // 平滑遷移:單用戶時代的舊 row 是 {exchange}:{date}。若當日已有舊基準就沿用它,
                // 不在部署當日把既有用戶的當日虧損基準重置成部署當下餘額(否則熔斷靈敏度被洗掉)。
                var legacy = _db.Get<BrokerCore.Models.PerpDailyOpenBalance>($"{exchange}:{utcDate}");
                var openingBalance = legacy != null && legacy.Balance > 0m ? legacy.Balance : currentEquity;
                _db.Insert(new BrokerCore.Models.PerpDailyOpenBalance
                {
                    Key = key,
                    Exchange = exchange,
                    UtcDate = utcDate,
                    Balance = openingBalance,
                    CapturedAt = DateTime.UtcNow,
                });
                _logger.LogInformation(
                    "Perp daily open balance recorded for {Owner}/{Ex} on {Date}: {Bal} USDT{Migrated}",
                    owner, exchange, utcDate, openingBalance, legacy != null ? " (sourced from legacy key)" : "");
                return ComputeDayPnlPct(currentEquity, openingBalance);
            }
            return ComputeDayPnlPct(currentEquity, existing.Balance);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ComputePerpDayPnlPct failed for {Ex}", exchange);
            return 0m;  // fail-safe：算不出來不誤觸熔斷
        }
    }

    private async Task PlacePerpOrderForSignalAsync(WatchItem item, string action, decimal confidence, string regimeType, CancellationToken ct)
    {
        if (!_registry.HasAvailableWorker("trading.perpetual"))
        {
            AddLog(item, "skip", "trading-worker has no perpetual capability (BingX disabled?)");
            return;
        }

        // A2.5b：以 watch.OwnerPrincipalId 找 user 自己的 BingX credential。沒設就 fallback env 預設 client。
        var creds = BuildCredentialsObject(item.OwnerPrincipalId, item.Exchange);

        // 拉現有部位（用 user credential、看得到 user 自己的倉）
        var posResult = await _dispatcher.DispatchAsync(BuildRequest("trading.perpetual", "get_positions",
            creds == null
                ? JsonSerializer.Serialize(new { exchange = item.Exchange })
                : JsonSerializer.Serialize(new { exchange = item.Exchange, __credentials = creds })));
        if (!posResult.Success)
        {
            AddLog(item, "skip", $"perp get_positions failed: {posResult.ErrorMessage}");
            return;
        }

        var posDoc = JsonDocument.Parse(posResult.ResultPayload ?? "{}").RootElement;
        decimal longQty = 0m, shortQty = 0m;
        int sameSymbolLongs = 0, sameSymbolShorts = 0;  // 計算 scale-in 用、含部分平倉後殘存
        if (posDoc.TryGetProperty("positions", out var posArr) && posArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in posArr.EnumerateArray())
            {
                var sym = p.TryGetProperty("symbol", out var s) ? s.GetString() : "";
                if (sym != item.Symbol) continue;
                var side = p.TryGetProperty("side", out var sd) ? sd.GetString() : "";
                var qty = p.TryGetProperty("quantity", out var q) ? q.GetDecimal() : 0m;
                if (side == "long")  { longQty = qty; sameSymbolLongs++; }
                else if (side == "short") { shortQty = qty; sameSymbolShorts++; }
            }
        }

        // Scale-in 門檻：同 symbol 已有同方向倉時、再加倉需要更高 confidence。
        // 公式：required = base + (existing_same_side_count × step)
        // 預設 base=0.5 / step=0.15 → 0 倉=0.50、1 倉=0.65、2 倉=0.80、3 倉=0.95
        // 單一倉位的場景下這條完全不影響（existing=0、required=base）。
        decimal RequiredConfidence(int existingSameSide)
            => Math.Min(0.99m, _minConfidence + existingSameSide * _perpScaleInStep);

        // Map signal → (perpAction, sideOverride, positionSide, qtyOverride, reduceOnly)
        string? perpAction = null;
        string? perpSide = null;
        string? perpPosSide = null;
        decimal qtyToUse = item.Quantity;
        bool reduceOnly = false;

        if (action == "buy")
        {
            if (longQty > 0m)
            {
                // 已有 long、評估是否符合 scale-in 門檻。finding J:用累計加碼次數(hedge 合併成 1 筆、筆數會凍結)。
                var existingLong = ResolveScaleInExisting(sameSymbolLongs,
                    _scaleInCountByKey.GetValueOrDefault($"{item.OwnerPrincipalId}:{item.Exchange}:{item.Symbol}:long"));
                var required = RequiredConfidence(existingLong);
                if (confidence >= required)
                {
                    perpAction = $"scale_in_long ({existingLong}→{existingLong+1}, conf {confidence:P0}≥{required:P0})";
                    perpSide = "buy"; perpPosSide = "long"; reduceOnly = false;
                }
                else
                {
                    perpAction = $"skip:already-long, conf {confidence:P0} < scale-in threshold {required:P0} (need higher confidence to add to existing position)";
                }
            }
            else if (shortQty > 0m) { perpAction = "close_short"; perpSide = "buy";  perpPosSide = "short"; qtyToUse = shortQty; reduceOnly = true; }
            else                    { perpAction = "open_long";   perpSide = "buy";  perpPosSide = "long";  reduceOnly = false; }
        }
        else // sell
        {
            if (shortQty > 0m)
            {
                var existingShort = ResolveScaleInExisting(sameSymbolShorts,
                    _scaleInCountByKey.GetValueOrDefault($"{item.OwnerPrincipalId}:{item.Exchange}:{item.Symbol}:short"));
                var required = RequiredConfidence(existingShort);
                if (confidence >= required)
                {
                    perpAction = $"scale_in_short ({existingShort}→{existingShort+1}, conf {confidence:P0}≥{required:P0})";
                    perpSide = "sell"; perpPosSide = "short"; reduceOnly = false;
                }
                else
                {
                    perpAction = $"skip:already-short, conf {confidence:P0} < scale-in threshold {required:P0} (need higher confidence to add to existing position)";
                }
            }
            else if (longQty > 0m) { perpAction = "close_long"; perpSide = "sell"; perpPosSide = "long";  qtyToUse = longQty; reduceOnly = true; }
            else if (item.Mode == "perp_long_only") perpAction = "skip:long-only-no-open-short";
            else                    { perpAction = "open_short"; perpSide = "sell"; perpPosSide = "short"; reduceOnly = false; }
        }

        if (perpSide == null)
        {
            AddLog(item, "skip", $"perp[{item.Mode}] {action} → {perpAction}");
            return;
        }

        // ── Risk gate（只擋開倉、平倉永遠放行）──
        // 平倉（reduceOnly=true）必須一律允許——擋出場才是真風險。
        // finding A 修:broker-local 風控閘（fetch mark price / sizing / max倉 / 相關性 / funding /
        //   regime / cooldown / slippage / pre-flight / 區塊外的 bracket SL）一律執行、**不再綁
        //   risk-worker 在線**。只有 r14/r16 的 pre_perp_order dispatch 真的需要 risk-worker（見下方
        //   內層 if）。之前整段綁 HasAvailableWorker("risk.check") → risk-worker 離線（重啟是常態）
        //   時整批跳過 → markPrice=0 → 連 bracket SL 都沒掛 → 固定量裸倉。
        // markPrice 提到 method scope、給後面 slippage audit + bracket SL 用。
        decimal markPrice = 0m;
        if (!reduceOnly)
        {
            var mpResult = await _dispatcher.DispatchAsync(BuildRequest("trading.perpetual", "get_mark_price",
                JsonSerializer.Serialize(new { exchange = item.Exchange, symbol = item.Symbol })));
            if (mpResult.Success)
            {
                var mpDoc = JsonDocument.Parse(mpResult.ResultPayload ?? "{}").RootElement;
                if (mpDoc.TryGetProperty("mark_price", out var mp)) markPrice = mp.GetDecimal();
            }

            // 把 get_positions 結果整理成 risk-worker 接受的 perp snapshot 形狀
            var perpPositions = new List<object>();
            decimal balance = 0m, available = 0m, equity = 0m;  // equity = balance + 未實現浮虧(finding C/D 用)
            if (posDoc.TryGetProperty("positions", out var allPos) && allPos.ValueKind == JsonValueKind.Array)
            {
                foreach (var pp in allPos.EnumerateArray())
                {
                    var pq = pp.TryGetProperty("quantity",   out var pqv) ? pqv.GetDecimal() : 0m;
                    var pmk = pp.TryGetProperty("mark_price", out var pmkv) ? pmkv.GetDecimal() : 0m;
                    perpPositions.Add(new
                    {
                        symbol        = pp.TryGetProperty("symbol",       out var pps)  ? pps.GetString() : "",
                        exchange      = pp.TryGetProperty("exchange",     out var pex)  ? pex.GetString() : "",
                        position_side = pp.TryGetProperty("side",         out var pside)? pside.GetString() : "",
                        quantity      = pq,
                        mark_price    = pmk,
                        notional      = pq * pmk,
                        leverage      = pp.TryGetProperty("leverage",     out var pl) && pl.TryGetInt32(out var pli) ? pli : 1,
                        liquidation_distance_pct = pp.TryGetProperty("liquidation_distance_pct", out var pld) ? pld.GetDecimal() : 0m,
                    });
                }
            }
            // account 也撈一下、給未來規則用（目前 3 條規則沒用到 balance、但留 payload 給後續）
            // 帶 user 自己的 credential、balance 才是 user 真實帳戶餘額
            var accResult = await _dispatcher.DispatchAsync(BuildRequest("trading.perpetual", "get_account",
                creds == null
                    ? JsonSerializer.Serialize(new { exchange = item.Exchange })
                    : JsonSerializer.Serialize(new { exchange = item.Exchange, __credentials = creds })));
            if (accResult.Success)
            {
                var ad = JsonDocument.Parse(accResult.ResultPayload ?? "{}").RootElement;
                if (ad.TryGetProperty("balance",          out var bv))  balance   = bv.GetDecimal();
                if (ad.TryGetProperty("available_margin", out var amv)) available = amv.GetDecimal();
                if (ad.TryGetProperty("equity",           out var ev))  equity    = ev.GetDecimal();
            }
            // finding C:equity 缺(舊 worker / 欄位沒回)→ 退回 balance(不比現況差;但 r16 仍看不到浮虧、bug 未修)。
            // 部署前用 docker exec broker curl loopback 打一次 get_account 確認回應含非零 equity。
            if (equity <= 0m) equity = balance;

            // 申報資金錨定：跌會收緊、漲不放寬。0 / 沒設 = 用實際 balance。
            var anchoredBalance = balance;
            var declared = ResolveDeclaredCapital(item.OwnerPrincipalId, item.Exchange);
            if (declared > 0m)
                anchoredBalance = Math.Min(balance, declared);

            // ── Portfolio circuit breaker (finding D)──
            // 組合層 equity 從「當日峰值」回落 ≥ 閾值% → 全面停開新倉(既有部位 SL/peak-trail 不動)。
            // 跟 r16(當日 loss-from-open)互補:r16 看「跌破開盤多少」、這條看「從盤中高點回落多少」。
            // 餵 equity(=balance+未實現浮虧,跟 finding C 同尺)、不是 anchoredBalance,否則對盤中浮虧回吐視而不見。
            // 多用戶:per-(owner:exchange) scope(BuildCbScopeKey),否則同 exchange 不同用戶 equity 互相污染 peak。
            // 只擋全新 open / scale_in;平倉 reduceOnly 走不到這裡。opt-in(_perpDdCbEnabled、預設 off)。
            if (_perpDdCbEnabled && equity > 0m
                && (perpAction!.StartsWith("open_") || perpAction.StartsWith("scale_in_")))
            {
                var cbScope = BuildCbScopeKey(item.OwnerPrincipalId, item.Exchange);
                var cb = EvaluateCircuitBreaker(cbScope, equity, DateTime.UtcNow);
                if (cb.Triggered)
                {
                    _logger.LogWarning(
                        "⚠ Perp circuit breaker on {Scope}: DD {Dd}% ≥ {Threshold}% (peak={Peak:F2}, now={Cur:F2}). Skipping {Symbol} {Action}.",
                        cbScope, cb.DdPct, cb.Threshold, cb.PeakValue, cb.CurrentValue, item.Symbol, perpAction);
                    AddLog(item, "halt",
                        $"⚠ Portfolio equity DD {cb.DdPct:F1}% ≥ {cb.Threshold}% on {item.Exchange} (peak={cb.PeakValue:F2}, now={cb.CurrentValue:F2}) — perp open blocked");
                    _ = TryPushCbAlertAsync(cbScope, item.Exchange, cb, ct);
                    return;
                }
            }

            // ★ Dynamic position sizing（user request）：開倉時動態算 qty 讓 max_loss = balance × risk%
            //
            //   per-trade   max_loss = balance × _dynamicRiskPct%       (預設 2%、對齊 r14)
            //   portfolio   max_loss = balance × _maxPortfolioRiskPct%  (預設 6%、對齊 r16)
            //
            //   existing_risk = Σ (已開倉 notional × InitialSlPct%)
            //   remaining_budget = portfolio_max - existing_risk
            //   allowed = min(per_trade, remaining_budget)
            //   notional = allowed / SL%
            //   qty = notional / markPrice
            //
            // 只對「開倉 + scale_in」生效；close / scale_out 維持既有 qty（要平多少平多少）。
            // markPrice 或 balance = 0 時 fallback 到 watch.Quantity 避免 broker 卡住。
            // ── Regime filter gate（HighVol / Squeeze 全擋、Trending 擋反向）─
            // 模式 off=不擋；soft=只 log；strict=擋並 return。只對開倉 / 加倉生效；平倉不受影響。
            if (_regimeGateMode != "off" && !string.IsNullOrEmpty(regimeType) &&
                (perpAction!.StartsWith("open_") || perpAction.StartsWith("scale_in_")))
            {
                var rt = regimeType.ToLowerInvariant();
                string? blockReason = rt switch
                {
                    "highvol" => $"regime=HighVol ATR 過大、{perpAction} 易被掃 SL",
                    "squeeze" => $"regime=Squeeze BB 過窄、{perpAction} 方向未明",
                    "trendingup"   when perpAction.Contains("short") => "regime=TrendingUp 趨勢向上、不開空",
                    "trendingdown" when perpAction.Contains("long")  => "regime=TrendingDown 趨勢向下、不開多",
                    _ => null,   // rangebound / unclear / 同方向趨勢都放行
                };
                if (blockReason != null)
                {
                    if (_regimeGateMode == "strict")
                    {
                        AddLog(item, "skip", $"regime-gate(strict): {blockReason}");
                        return;
                    }
                    // soft：只觀察、不擋
                    AddLog(item, "warn", $"regime-gate(soft): {blockReason} — 仍放行");
                }
            }

            // ── Per-symbol entry cooldown（避免訊號反翻 churn）─
            // 對 open_* / scale_in_* 都生效；close / protect / reduce_only 由 perpAction 不以 open_ 起頭排除。
            var cdKey = $"{item.Exchange}:{item.Symbol}";
            if (_minEntryIntervalSec > 0 &&
                (perpAction!.StartsWith("open_") || perpAction.StartsWith("scale_in_")) &&
                _lastEntryAt.TryGetValue(cdKey, out var lastEntry))
            {
                var elapsed = (DateTime.UtcNow - lastEntry).TotalSeconds;
                if (elapsed < _minEntryIntervalSec)
                {
                    AddLog(item, "skip",
                        $"entry cooldown: last {(int)elapsed}s ago < {_minEntryIntervalSec}s. Action {perpAction} suppressed.");
                    return;
                }
            }

            // ── C1 — Slippage backoff gate（上一筆執行品質差就 cooldown 同 symbol 新單）─
            // 平倉（reduceOnly）不擋 — 出場永遠優先；只對 open_*/scale_in_* 生效。
            // gate state 移到 AutoTraderSizingService、觸發在後面 slippage observation 寫進去。
            if (!reduceOnly &&
                (perpAction!.StartsWith("open_") || perpAction.StartsWith("scale_in_")) &&
                _sizing.IsInSlippageBackoff(item.Exchange, item.Symbol, out var remainMin))
            {
                AddLog(item, "skip",
                    $"slippage backoff: last fill slippage > {_slippageAlertPct:F2}%, " +
                    $"cooling down {remainMin} more min (self-healing on poor execution quality).");
                return;
            }

            // ── Hard cap on simultaneous open positions（user request 「3 倉不會再多」）─
            // 只擋全新 open；scale_in 是加碼同向同 symbol、不增加 unique position count
            if (_maxOpenPositions > 0 && perpAction!.StartsWith("open_") &&
                perpPositions.Count >= _maxOpenPositions)
            {
                AddLog(item, "skip",
                    $"max {_maxOpenPositions} open positions reached ({perpPositions.Count} existing). New symbol open blocked.");
                return;
            }

            // ── 30-day correlation cap（防 BTC/ETH/SOL 同向 3 倉 = 1 倉 beta）─
            // 只對「全新 open」啟用；scale_in 是同 symbol、自然 corr=1.0 不該擋自己
            if (_maxCorrelation > 0m && perpAction!.StartsWith("open_") && perpPositions.Count > 0)
            {
                var corrResult = await CheckCorrelationCap(item, perpPositions, ct);
                if (corrResult.shouldBlock)
                {
                    AddLog(item, "skip",
                        $"correlation cap: |r|={corrResult.maxCorr:F3} vs {corrResult.mostCorrelated} > {_maxCorrelation:F2} (3 倉變 1 倉風險)");
                    return;
                }
                else if (corrResult.maxCorr > 0m)
                {
                    // 沒擋但記錄、給 dashboard / 之後 thesis 評估
                    AddLog(item, "info",
                        $"correlation OK: |r|={corrResult.maxCorr:F3} vs {corrResult.mostCorrelated} (threshold {_maxCorrelation:F2})");
                }
            }

            // ── Funding rate cap（cost-aware sizing、擠擁 funding 直接拒絕對應方向）─
            // 拉 BingX premiumIndex public endpoint、看 lastFundingRate。
            // funding > +threshold → 拒 long；funding < -threshold → 拒 short
            // 只對 open_ 啟用、不限制 scale_in（避免半路被 funding spike 趕出場）
            if (_maxFundingRatePct > 0m && perpAction!.StartsWith("open_") && item.Exchange.Equals("bingx", StringComparison.OrdinalIgnoreCase))
            {
                var fr = await FetchFundingRate(item.Symbol, ct);
                if (fr.HasValue)
                {
                    var frPct = fr.Value * 100m;   // BingX 回 raw decimal、× 100 變百分比
                    var isLong = perpAction.Contains("long");
                    if (isLong && frPct > _maxFundingRatePct)
                    {
                        AddLog(item, "skip",
                            $"funding cap: rate={frPct:F4}% / 8h > +{_maxFundingRatePct:F2}% → 開多太貴、拒絕");
                        return;
                    }
                    if (!isLong && frPct < -_maxFundingRatePct)
                    {
                        AddLog(item, "skip",
                            $"funding cap: rate={frPct:F4}% / 8h < -{_maxFundingRatePct:F2}% → 開空太貴、拒絕");
                        return;
                    }
                }
            }

            // ── 全倉曝險比例 sizing（opt-in、優先於 risk/SL sizing）─
            // notional = exposurePct% × 帳戶總資金;組合曝險上限用 _maxPortfolioExposurePct(notional 總和 / balance)。
            // cross margin 無 per-position 強平 → 用「總曝險 / 總資金」當風控刻度,不靠 per-trade SL。
            // 資金預算制:per-watch budget_pct > 0 覆蓋全域 exposure_pct(多倉各配固定額度、不先出訊號先搶光)
            var effExposurePct = item.BudgetPct > 0m ? item.BudgetPct : _exposurePct;
            if (effExposurePct > 0m && (perpAction!.StartsWith("open_") || perpAction.StartsWith("scale_in_")))
            {
                // 已開倉曝險總和（各倉 notional 直接相加）
                decimal existingNotional = 0m;
                foreach (var pp in perpPositions)
                {
                    var notionalAnonProp = pp.GetType().GetProperty("notional")?.GetValue(pp);
                    if (notionalAnonProp is decimal n) existingNotional += n;
                }

                // 純算法抽到 ComputeExposureSizing(好測、真錢路徑邊界有單測覆蓋)
                var sizing = ComputeExposureSizing(
                    anchoredBalance, markPrice, existingNotional,
                    effExposurePct, _maxPortfolioExposurePct, item.Leverage);

                if (!sizing.Applicable)
                {
                    _logger.LogWarning("AutoTrader exposure sizing skipped {Symbol}: balance={Bal} mark={Mark} (using watch.Quantity {Qty})",
                        item.Symbol, anchoredBalance, markPrice, qtyToUse);
                }
                else if (sizing.BudgetExhausted)
                {
                    AddLog(item, "skip",
                        $"exposure budget exhausted: existing_notional={existingNotional:F2} ≥ portfolio_max={sizing.PortfolioMaxNotional:F2} ({_maxPortfolioExposurePct}% of ${anchoredBalance:F2})");
                    return;
                }
                else
                {
                    if (sizing.MarginClamped)
                        AddLog(item, "info",
                            $"exposure clamped to margin cap: → {sizing.AllowedNotional:F2} (balance ${anchoredBalance:F2} × {item.Leverage}x × 0.95)");
                    _logger.LogInformation(
                        "AutoTrader exposure sizing {Symbol}: balance={Bal:F2} exposure={Exp}%{BudgetTag} existing_notional={Exist:F2} max={Max:F2} → allowed_notional={All:F2} lev={Lev}x mark={Mark:F4} qty={Qty:F6} margin={Margin:F2}",
                        item.Symbol, anchoredBalance, effExposurePct, item.BudgetPct > 0m ? "(budget)" : "", existingNotional, sizing.PortfolioMaxNotional,
                        sizing.AllowedNotional, item.Leverage, markPrice, sizing.Qty, sizing.AllowedNotional / Math.Max(item.Leverage, 1));
                    qtyToUse = sizing.Qty;
                }
            }
            else if (_dynamicRiskPct > 0m && (perpAction!.StartsWith("open_") || perpAction.StartsWith("scale_in_")))
            {
                // 已開倉的累計風險：每倉 notional × SL%（反射讀 anon 物件留在 caller、純函式只收算好的數字）
                decimal existingRisk = 0m;
                foreach (var pp in perpPositions)
                {
                    var notionalAnonProp = pp.GetType().GetProperty("notional")?.GetValue(pp);
                    if (notionalAnonProp is decimal n)
                        existingRisk += n * (_protectionConfig.InitialSlPct / 100m);
                }

                // finding G:純算法抽到 ComputeDynamicRiskSizing(含保證金硬上限 clamp、對齊 ComputeExposureSizing)。
                // 之前 inline 算 maxNotional=allowedRisk/SL% 沒 cap → 小 SL% + 低槓桿時 notional 爆保證金被拒單。
                var sizing = ComputeDynamicRiskSizing(
                    anchoredBalance, markPrice, _protectionConfig.InitialSlPct,
                    existingRisk, _dynamicRiskPct, _maxPortfolioRiskPct, item.Leverage);

                if (!sizing.Applicable)
                {
                    _logger.LogWarning("AutoTrader dynamic sizing skipped {Symbol}: balance={Bal} mark={Mark} sl={Sl} (using watch.Quantity {Qty})",
                        item.Symbol, anchoredBalance, markPrice, _protectionConfig.InitialSlPct, qtyToUse);
                }
                else if (sizing.BudgetExhausted)
                {
                    AddLog(item, "skip",
                        $"portfolio risk budget exhausted: existing_risk={existingRisk:F2} ≥ portfolio_max={sizing.PortfolioMax:F2} ({_maxPortfolioRiskPct}% of ${anchoredBalance:F2})");
                    return;
                }
                else
                {
                    if (sizing.MarginClamped)
                        AddLog(item, "info",
                            $"dynamic sizing clamped to margin cap: → {sizing.AllowedNotional:F2} (balance ${anchoredBalance:F2} × {item.Leverage}x × 0.95)");
                    _logger.LogInformation(
                        "AutoTrader dynamic sizing {Symbol}: balance={Bal:F2} existing_risk={Exist:F2} allowed={All:F2} (per-trade {Pt:F2}, portfolio max {Pm:F2}) sl={Sl}% mark={Mark:F4} lev={Lev}x → notional={Not:F2} qty={Qty:F6} margin={Margin:F2}",
                        item.Symbol, anchoredBalance, existingRisk, sizing.AllowedRisk,
                        sizing.PerTradeMax, sizing.PortfolioMax, _protectionConfig.InitialSlPct, markPrice,
                        item.Leverage, sizing.AllowedNotional, sizing.Qty, sizing.AllowedNotional / Math.Max(item.Leverage, 1));
                    qtyToUse = sizing.Qty;
                }
            }

            // ── C2 — Adaptive sizing（delegate to AutoTraderSizingService）─
            // 只對 open / scale-in 套用、平倉不縮（出場永遠用足量）。Service 內部判斷是否啟用。
            if (!reduceOnly && (perpAction!.StartsWith("open_") || perpAction.StartsWith("scale_in_")))
            {
                var before = qtyToUse;
                var (sized, tags) = _sizing.ApplyForSignal(qtyToUse, confidence, item.Exchange, item.Symbol, item.Strategy);
                qtyToUse = sized;
                if (qtyToUse != before)
                {
                    var factorPct = before > 0m ? qtyToUse / before : 1m;
                    AddLog(item, "info",
                        $"adaptive sizing: {tags} factor={factorPct:P0} " +
                        $"qty {before:F6} → {qtyToUse:F6}");
                }
            }

            // ── 數量精度對齊：開倉 qty round DOWN 到 QtyStep（跟 price tick 同一類、避免 BingX 拒單）。
            // 平倉不動（用交易所回報的實際 position qty、本來就合法精度）。在 pre-flight 前做、查的是 rounded qty。
            if (!reduceOnly)
            {
                var qspec = BrokerCore.Trading.SymbolSpecs.GetSpec(item.Exchange, item.Symbol);
                if (qspec != null && qspec.QtyStep > 0m)
                {
                    var beforeQ = qtyToUse;
                    qtyToUse = RoundQtyToStep(qtyToUse, qspec.QtyStep);
                    if (qtyToUse != beforeQ)
                        AddLog(item, "info", $"qty rounded to step {qspec.QtyStep}: {beforeQ:F6} → {qtyToUse:F6}");
                }
            }

            // ── Pre-flight：dynamic sizing 完才知 qty、在這裡查 BingX min order / leverage spec
            // user request：條件不過直接擋下、不要走完整鏈才爆
            if (!reduceOnly)
            {
                var (preOk, preErr, preWarn) = BrokerCore.Trading.SymbolSpecs.PreflightOrder(
                    item.Exchange, item.Symbol, qtyToUse, item.Leverage, markPrice > 0m ? markPrice : null);
                if (!preOk)
                {
                    AddLog(item, "skip", $"pre-flight rejected: {preErr}");
                    return;
                }
                if (!string.IsNullOrEmpty(preWarn))
                    _logger.LogInformation("AutoTrader pre-flight warning {Sym}: {Warn}", item.Symbol, preWarn);
            }

            // ── risk-worker 端風控（r14 per-trade max-loss / r16 daily CB / pre_perp_order）──
            // finding A:**只有這段真的需要 risk-worker**。broker-local 閘 + sizing 已在上面一律執行。
            //   risk-worker 在線時行為完全不變;離線時(見 else):qty 仍 sized、max倉/相關性/funding 仍擋,
            //   只少 r14/r16,而 r14 per-trade 損失上限由交易所端 bracket SL 近似涵蓋 —— 但這個「不裸奔」
            //   保證只在 bracket SL 已啟用時成立,所以 else 分支會在「離線 且 SL 未啟用」時 fail-closed 擋開倉。
            var openRiskMode = ResolvePerpOpenRiskMode(_registry.HasAvailableWorker("risk.check"), _bracketSlEnabled);
            if (openRiskMode == PerpOpenRiskMode.FailClosedBlock)
            {
                // 離線 且 bracket SL 未啟用 → 無 r14/r16 又無交易所止損 = 真裸倉 → fail-closed 擋下。
                // 只擋這個組合;bracket SL 有開(生產)就照放、不誤 halt。平倉 reduceOnly 走不到這裡、永遠放行。
                AddLog(item, "blocked", "risk-worker 離線 且 bracket SL 未啟用(AUTOTRADER_BRACKET_SL_ENABLED≠true):開倉會是無止損裸倉 → fail-closed 擋下。平倉不受影響。");
                _logger.LogWarning("AutoTrader perp open BLOCKED (fail-closed): risk-worker offline AND bracket SL disabled → would be naked entry (no r14/r16, no exchange SL). {Symbol} on {Exchange}", item.Symbol, item.Exchange);
                return;
            }
            if (openRiskMode == PerpOpenRiskMode.RunRiskWorker)
            {
                // r16 daily loss circuit breaker：取「今日 UTC 開盤 balance」、算當日 PnL%
                // finding C:餵 equity(=balance+未實現浮虧)、不是 wallet balance,否則抱浮虧倉時 r16 永不觸發。
                var dayPnlPct = ComputePerpDayPnlPct(item.OwnerPrincipalId, item.Exchange, equity);

                var riskPayload = JsonSerializer.Serialize(new
                {
                    symbol        = item.Symbol,
                    exchange      = item.Exchange,
                    side          = perpSide,
                    position_side = perpPosSide,
                    quantity      = qtyToUse,
                    price         = markPrice,
                    leverage      = item.Leverage,
                    initial_sl_pct = _protectionConfig.InitialSlPct,   // 給 r14 max_loss_per_trade_pct 用
                    perp = new { balance = anchoredBalance, available_margin = available, day_pnl_pct = dayPnlPct, positions = perpPositions },
                });
                var riskResult = await _dispatcher.DispatchAsync(BuildRequest("risk.check", "pre_perp_order", riskPayload));
                if (riskResult.Success)
                {
                    var riskDoc = JsonDocument.Parse(riskResult.ResultPayload ?? "{}").RootElement;
                    var passed = riskDoc.TryGetProperty("passed", out var rp) && rp.GetBoolean();
                    if (!passed)
                    {
                        var msgs = new List<string>();
                        if (riskDoc.TryGetProperty("violations", out var vs) && vs.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var v in vs.EnumerateArray())
                                if (v.TryGetProperty("message", out var vm)) msgs.Add(vm.GetString() ?? "");
                        }
                        var summary = msgs.Count > 0 ? string.Join("; ", msgs) : "perp risk rejected";
                        AddLog(item, "blocked", $"PERP risk blocked: {summary}");
                        _logger.LogWarning("AutoTrader perp risk blocked {Symbol} on {Exchange}: {Msg}", item.Symbol, item.Exchange, summary);
                        return;
                    }
                }
                // riskResult.Success == false → risk-worker 在線但回錯；保留 fail-open 行為跟 spot 路徑一致
            }
            else // ProceedWithBracketSl:risk-worker 離線、但 bracket SL 已啟用 → 放行(交易所端 SL 守)
            {
                // r14/r16 跳過,但 broker-local 閘已執行 + bracket SL 會在下面掛上(markPrice 已有值)。
                // 「無 SL 的裸倉」組合在前面 FailClosedBlock 已擋掉、走不到這裡。明確記錄供 audit。
                AddLog(item, "warn", "risk-worker 離線:r14/r16/pre_perp_order 跳過(broker-local 閘 + bracket SL 仍生效);新開倉風險由交易所端 SL 與本地硬上限保護");
                _logger.LogWarning("AutoTrader perp open without risk-worker validation (r14/r16 skipped) {Symbol} on {Exchange} — broker-local gates + bracket SL still applied", item.Symbol, item.Exchange);
            }
        }

        // 真開單——帶 user credential 才會走到 user 自己的帳戶
        // strategy: 不論開倉或平倉都帶上 watch 的 strategy；同一倉位平倉的 trade row 才能對應回原策略
        // 用 Dictionary 而非 anonymous object、方便條件式加 bracket SL / creds。
        var perpDict = new Dictionary<string, object?>
        {
            ["exchange"]      = item.Exchange,
            ["symbol"]        = item.Symbol,
            ["side"]          = perpSide,
            ["position_side"] = perpPosSide,
            ["order_type"]    = "market",
            ["quantity"]      = qtyToUse,
            ["leverage"]      = item.Leverage,
            ["reduce_only"]   = reduceOnly,
            ["strategy"]      = item.Strategy,
        };
        if (creds != null) perpDict["__credentials"] = creds;
        // 冪等 key（gated:AUTOTRADER_IDEMPOTENT_KEYS=1 才非 null）:同一開倉意圖 → 同 key → failover 重送被 BingX 擋（code=101400）、不雙開倉
        var openIdemKey = BuildIdemKey("op", item.OwnerPrincipalId, item.Exchange, item.Symbol, perpPosSide ?? "", item.Strategy ?? "open");
        if (openIdemKey != null) perpDict["client_order_id"] = openIdemKey;

        // C — Bracket SL：開倉（非平倉）時帶 exchange-side stop_loss_price、broker downtime 也有保護。
        // 用 markPrice 近似 entry、依方向算 SL。markPrice<=0（沒拿到）就 skip、不擋下單。
        if (_bracketSlEnabled && !reduceOnly && markPrice > 0m
            && (perpAction!.StartsWith("open_") || perpAction.StartsWith("scale_in_")))
        {
            var isLong = string.Equals(perpPosSide, "long", StringComparison.OrdinalIgnoreCase);
            var pricePrecision = BrokerCore.Trading.SymbolSpecs.GetSpec(item.Exchange, item.Symbol)?.PricePrecision;
            // 2026-06-04:UseSignalSl 腿 → 用 strategy 結構性停損(諧波 PRZ 失效價)當 base,否則固定 %;都過 LeverageAwareSlPct clamp。
            var baseSlPct = ResolveBaseSlPct(markPrice, isLong, item.UseSignalSl, item.PendingSignalSl, _protectionConfig.InitialSlPct);
            var usingSignalSl = item.UseSignalSl && item.PendingSignalSl is decimal _psl && _psl > 0m
                                && (isLong ? _psl < markPrice : _psl > markPrice);
            var effectiveSlPct = LeverageAwareSlPct(baseSlPct, item.Leverage, _disableLevSlCap);
            var slPrice = ComputeBracketSlPrice(markPrice, effectiveSlPct, isLong);
            if (slPrice.HasValue)
            {
                var slRounded = RoundPrice(slPrice.Value, pricePrecision);
                perpDict["stop_loss_price"] = slRounded;
                // 橋接給保護 scan:讓 soft SL init 用同一個 signal stop(否則 soft 仍停在固定%、提前平倉、白費結構性停損)
                if (usingSignalSl)
                    _signalSlByKey[$"{item.OwnerPrincipalId}:{item.Exchange}:{item.Symbol}:{perpPosSide}"] = item.PendingSignalSl!.Value;
                var tightened = effectiveSlPct < baseSlPct;
                AddLog(item, "info",
                    $"bracket SL attached: entry≈{markPrice:F4} SL={slRounded:F4} ({effectiveSlPct:0.##}% {(isLong ? "below" : "above")}, {item.Leverage}x, mode={(usingSignalSl ? "signal-PRZ" : "fixed")}" +
                    (tightened ? $", clamped from {baseSlPct:0.##}% to stay inside liq distance)" : ")"));
            }

            // C — Bracket TP（opt-in）：R:R 模式優先（TP 距離 = RR × SL 距離）、否則固定 %；跟 trailing 並存。
            var tpPct = ResolveBracketTpPct(_bracketTpRr, _bracketTpPct, effectiveSlPct);
            if (tpPct > 0m)
            {
                var tpPrice = ComputeBracketTpPrice(markPrice, tpPct, isLong);
                if (tpPrice.HasValue)
                {
                    var tpRounded = RoundPrice(tpPrice.Value, pricePrecision);
                    perpDict["take_profit_price"] = tpRounded;
                    var tpMode = _bracketTpRr > 0m ? $"R:R {_bracketTpRr:0.##}×SL" : "fixed";
                    AddLog(item, "info",
                        $"bracket TP attached: entry≈{markPrice:F4} TP={tpRounded:F4} ({tpPct:0.##}% {(isLong ? "above" : "below")}, {tpMode})");
                }
            }
        }

        var perpPayload = JsonSerializer.Serialize(perpDict);
        var orderResult = await _dispatcher.DispatchAsync(BuildRequest("trading.perpetual", "place_order", perpPayload));
        if (orderResult.Success)
        {
            var ord = JsonDocument.Parse(orderResult.ResultPayload ?? "{}").RootElement;
            var status = ord.TryGetProperty("status", out var st) ? st.GetString() : "?";
            var extId = ord.TryGetProperty("external_id", out var ei) ? ei.GetString() : "?";
            AddLog(item, perpAction!, $"PERP {perpAction!.ToUpper()}: {extId} {perpSide} {qtyToUse} {item.Symbol} {item.Leverage}x → {status}");
            _logger.LogInformation("AutoTrader perp: {PerpAction} {Side} {Qty} {Symbol} on {Exchange} {Lev}x → {Status}",
                perpAction, perpSide, qtyToUse, item.Symbol, item.Exchange, item.Leverage, status);
            // finding I:進場成交但交易所 SL 沒掛上 → 明確告警,讓 broker soft-SL 保護鏈接管這個部位。
            if (status == "filled_no_sl")
            {
                AddLog(item, "warn", $"⚠ 交易所端 SL 掛單失敗(進場已成交)— broker soft-SL 必須涵蓋 {item.Symbol} {perpPosSide}");
                _logger.LogWarning("AutoTrader perp entry filled but exchange SL failed — broker soft-SL must cover {Symbol} {Pos} on {Exchange}",
                    item.Symbol, perpPosSide, item.Exchange);
            }

            // ── Slippage audit + C1 backoff trigger（filled_price vs signal markPrice）─
            // 當下單 — 已成交不可逆、不擋 dispatch；但同 symbol 下一個 open signal 進 backoff cooldown。
            // _slippageBackoffMin=0 退化成純 audit warn（向後相容）。
            if (_slippageAlertPct > 0m && markPrice > 0m && ord.TryGetProperty("filled_price", out var fp)
                && fp.ValueKind == JsonValueKind.Number && fp.GetDecimal() > 0m)
            {
                var filled = fp.GetDecimal();
                var slippagePct = Math.Abs(filled - markPrice) / markPrice * 100m;
                if (slippagePct > _slippageAlertPct)
                {
                    AddLog(item, "warn",
                        $"slippage {slippagePct:F3}% > {_slippageAlertPct:F2}% (signal mark {markPrice:F4} → filled {filled:F4})");

                    if (_sizing.SlippageBackoffMin > 0 && !reduceOnly)
                    {
                        _sizing.ArmSlippageBackoff(item.Exchange, item.Symbol);
                        var until = DateTime.UtcNow.AddMinutes(_sizing.SlippageBackoffMin);
                        AddLog(item, "warn",
                            $"slippage backoff armed for {_sizing.SlippageBackoffMin} min on {item.Exchange}:{item.Symbol} " +
                            $"(next open signal will skip until {until:HH:mm} UTC).");
                    }
                }
            }

            // Cooldown 紀錄：只在「成功開倉 / scale_in」更新時間戳；close / reduce-only 不更新
            // （平倉後立即反向開倉是合理場景、不該被 cooldown 擋）
            if (!reduceOnly && (perpAction!.StartsWith("open_") || perpAction.StartsWith("scale_in_")))
            {
                _lastEntryAt[$"{item.Exchange}:{item.Symbol}"] = DateTime.UtcNow;
                // finding J:只在真的 scale_in 成功才 +1(open_ 不加、靠 ResolveScaleInExisting 的 max 兜底成 1,避免 off-by-one 把門檻多升一級)
                if (perpAction.StartsWith("scale_in_"))
                    _scaleInCountByKey.AddOrUpdate(
                        $"{item.OwnerPrincipalId}:{item.Exchange}:{item.Symbol}:{perpPosSide}", 1, (_, v) => v + 1);
            }
        }
        else
        {
            AddLog(item, "error", $"perp order failed: {orderResult.ErrorMessage}");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// 算 deterministic client_order_id —— 拆出來方便單測 + 文檔化合約規則：
    ///   - 同 (exchange, symbol, action, quantity, bucket) 永遠回同一個 key
    ///   - bucket 不同 → key 不同（跨時間視窗的新意圖不會被 dedup）
    ///   - dot 換 underscore（Binance newClientOrderId 限 [a-zA-Z0-9-_]）
    ///   - 截到 36 char（Binance 上限）
    /// </summary>
    internal static string BuildAutoOrderKey(string exchange, string symbol, string action, decimal quantity, long bucket)
    {
        var rawKey = $"auto-{exchange}-{symbol}-{action}-{quantity:G}-{bucket}".Replace('.', '_');
        return rawKey.Length > 36 ? rawKey[..36] : rawKey;
    }

    /// <summary>
    /// C — 算開倉時帶的 exchange-side bracket SL 價格。
    ///   long  → entry × (1 − slPct/100)  （SL 在進場價下方）
    ///   short → entry × (1 + slPct/100)  （SL 在進場價上方）
    /// entry 用 markPrice 近似（market order 成交價接近 mark）。
    /// 回 null = 不該帶（entry/slPct 無效）。pure static、好測。
    /// </summary>
    internal static decimal? ComputeBracketSlPrice(decimal entryPrice, decimal slPct, bool isLong)
    {
        if (entryPrice <= 0m || slPct <= 0m) return null;
        var sl = isLong
            ? entryPrice * (1m - slPct / 100m)
            : entryPrice * (1m + slPct / 100m);
        return sl > 0m ? Math.Round(sl, 6) : null;
    }

    /// <summary>
    /// C — 槓桿感知 SL 距離：高槓桿時把 SL 強制收緊到強平距離以內，避免 SL 掛在強平價之後形同虛設
    /// （20x 用 5% SL ≈ 掛在強平價上、會先爆倉 SL 才沒救）。
    ///   強平距離(%) ≈ 100 / leverage；SL 必須先於強平觸發，取 強平距離 × 0.6（留 40% buffer 吸收
    ///   維持保證金 + 手續費誤差）。回 min(設定值, 強平距離 × 0.6)。leverage ≤ 1 → 不收緊（現貨/無槓桿）。
    /// pure static、好測。
    /// </summary>
    /// <summary>
    /// 全倉曝險比例 sizing 的純算法結果。Applicable=false → balance/mark 無效、caller 退回 watch.Quantity;
    /// BudgetExhausted=true → 組合曝險預算用完、caller skip;否則用 Qty 下單(MarginClamped 表示被保證金 cap 砍過)。
    /// </summary>
    internal readonly record struct ExposureSizingResult(
        bool Applicable, bool BudgetExhausted, bool MarginClamped,
        decimal Qty, decimal AllowedNotional, decimal PortfolioMaxNotional);

    /// <summary>
    /// 全倉曝險比例 sizing(pure、好測):notional = exposurePct% × 帳戶總資金,夾在「組合曝險預算」與
    /// 「保證金硬上限 balance × leverage × 0.95」之內。caller 已保證 exposurePct &gt; 0。
    ///   - balance ≤ 0 或 mark ≤ 0 → Applicable=false(無法算、退回固定量)
    ///   - 扣掉已開倉 notional 後預算 ≤ 0 → BudgetExhausted=true(略過)
    ///   - 否則回 Qty = allowedNotional / mark;allowedNotional 超過保證金 cap 就 clamp(MarginClamped=true)
    /// </summary>
    internal static ExposureSizingResult ComputeExposureSizing(
        decimal anchoredBalance, decimal markPrice, decimal existingNotional,
        decimal exposurePct, decimal maxPortfolioExposurePct, decimal leverage)
    {
        if (anchoredBalance <= 0m || markPrice <= 0m)
            return new ExposureSizingResult(Applicable: false, false, false, 0m, 0m, 0m);

        var perTradeNotional = anchoredBalance * (exposurePct / 100m);
        var portfolioMaxNotional = maxPortfolioExposurePct > 0m
            ? anchoredBalance * (maxPortfolioExposurePct / 100m)
            : decimal.MaxValue;
        var remaining = portfolioMaxNotional - existingNotional;
        var allowedNotional = Math.Min(perTradeNotional, Math.Max(0m, remaining));

        if (allowedNotional <= 0m)
            return new ExposureSizingResult(true, BudgetExhausted: true, false, 0m, 0m, portfolioMaxNotional);

        // 保證金硬上限:即使全倉,notional 不能超過 balance × leverage(否則交易所保證金不足拒單)。
        // 留 5% buffer 給手續費 / 滑價。
        var marginCap = anchoredBalance * Math.Max(leverage, 1m) * 0.95m;
        bool clamped = allowedNotional > marginCap;
        if (clamped) allowedNotional = marginCap;

        return new ExposureSizingResult(true, false, clamped, allowedNotional / markPrice, allowedNotional, portfolioMaxNotional);
    }

    /// <summary>
    /// 動態風險 sizing 純算法結果（finding G）。Applicable=false → 輸入無效、caller 退回固定量;
    /// BudgetExhausted=true → 組合風險預算用完、caller skip;否則用 Qty 下單
    /// (MarginClamped=true 表示 notional 被保證金硬上限砍過、避免被交易所拒單)。
    /// </summary>
    internal readonly record struct DynamicRiskSizingResult(
        bool Applicable, bool BudgetExhausted, bool MarginClamped,
        decimal Qty, decimal AllowedNotional, decimal AllowedRisk,
        decimal PerTradeMax, decimal PortfolioMax);

    /// <summary>
    /// 動態風險 sizing(pure、好測):per-trade max_loss = balance × dynamicRiskPct%,
    /// notional = allowedRisk / (slPct/100),夾在「組合風險預算」與「保證金硬上限 balance × leverage × 0.95」之內。
    /// caller 已保證 dynamicRiskPct &gt; 0;existingRisk 由 caller 算好傳入(反射讀 anon 物件不放純函式裡)。
    ///   - balance ≤ 0 或 mark ≤ 0 或 slPct ≤ 0 → Applicable=false(退回固定量)
    ///   - 扣掉已開倉累計風險後預算 ≤ 0 → BudgetExhausted=true(略過)
    ///   - 否則 Qty = allowedNotional / mark;notional 超過保證金 cap 就 clamp(MarginClamped=true)
    /// finding G:之前 dynamic 路徑漏了這個 marginCap clamp(exposure 路徑有)→ 小 SL% + 低槓桿時 notional 爆保證金。
    /// </summary>
    internal static DynamicRiskSizingResult ComputeDynamicRiskSizing(
        decimal anchoredBalance, decimal markPrice, decimal slPct,
        decimal existingRisk, decimal dynamicRiskPct, decimal maxPortfolioRiskPct,
        decimal leverage)
    {
        if (anchoredBalance <= 0m || markPrice <= 0m || slPct <= 0m)
            return new DynamicRiskSizingResult(Applicable: false, false, false, 0m, 0m, 0m, 0m, 0m);

        var perTradeMax = anchoredBalance * (dynamicRiskPct / 100m);
        var portfolioMax = maxPortfolioRiskPct > 0m
            ? anchoredBalance * (maxPortfolioRiskPct / 100m)
            : decimal.MaxValue;
        var remainingBudget = portfolioMax - existingRisk;
        var allowedRisk = Math.Min(perTradeMax, Math.Max(0m, remainingBudget));

        if (allowedRisk <= 0m)
            return new DynamicRiskSizingResult(true, BudgetExhausted: true, false, 0m, 0m, 0m, perTradeMax, portfolioMax);

        var allowedNotional = allowedRisk / (slPct / 100m);

        // 保證金硬上限(對齊 ComputeExposureSizing):notional 不能超過 balance × leverage × 0.95、否則保證金不足被拒單。
        // 小 SL% + 低槓桿時 risk 反推的 notional 會爆掉這條(notional 與 SL% 成反比)。
        var marginCap = anchoredBalance * Math.Max(leverage, 1m) * 0.95m;
        bool clamped = allowedNotional > marginCap;
        if (clamped) allowedNotional = marginCap;

        return new DynamicRiskSizingResult(true, false, clamped,
            allowedNotional / markPrice, allowedNotional, allowedRisk, perTradeMax, portfolioMax);
    }

    internal static decimal LeverageAwareSlPct(decimal configuredSlPct, decimal leverage, bool disableCap = false)
    {
        // 全倉模式:cross margin 無 per-position 強平,SL 不必收緊到強平距離內 →
        // 用 configured 值、讓部位抱深回撤等反彈(貼近「無停損」回測過的 edge)。
        if (disableCap) return configuredSlPct;
        if (leverage <= 1m) return configuredSlPct;
        var liqDistanceCap = 100m / leverage * 0.6m;
        return Math.Min(configuredSlPct, liqDistanceCap);
    }

    /// <summary>
    /// 2026-06-04:決定初始 SL 的「base 距離 %」(尚未過 LeverageAwareSlPct clamp)。
    ///   useSignalSl + signalStop 在 entry 正確一側(long 在下/short 在上)且 &gt;0
    ///     → 回 signal 隱含風險%(諧波 PRZ 失效價等結構性停損),讓策略自己的論點失效價當停損;
    ///   否則(沒開、無 emit、或方向錯)→ 回 fixedFallbackPct(既有固定 %/ATR 行為)。
    /// 回傳值仍會在呼叫端過 LeverageAwareSlPct 收緊到強平距離內 → 即使 signal stop 過寬也不會超過災難線(安全不變)。
    /// pure static、好測。
    /// </summary>
    internal static decimal ResolveBaseSlPct(
        decimal entryPrice, bool isLong, bool useSignalSl, decimal? signalStop, decimal fixedFallbackPct)
    {
        if (useSignalSl && signalStop is decimal s && s > 0m && entryPrice > 0m)
        {
            bool correctSide = isLong ? s < entryPrice : s > entryPrice;
            if (correctSide)
            {
                var riskPct = Math.Abs(entryPrice - s) / entryPrice * 100m;
                if (riskPct > 0m) return riskPct;
            }
        }
        return fixedFallbackPct;
    }

    /// <summary>
    /// C — 算開倉時帶的 exchange-side bracket TP 價格（SL 的鏡像）。
    ///   long  → entry × (1 + tpPct/100)  （TP 在進場價上方）
    ///   short → entry × (1 − tpPct/100)  （TP 在進場價下方）
    /// 回 null = 不該帶（entry/tpPct 無效）。pure static、好測。
    /// </summary>
    internal static decimal? ComputeBracketTpPrice(decimal entryPrice, decimal tpPct, bool isLong)
    {
        if (entryPrice <= 0m || tpPct <= 0m) return null;
        var tp = isLong
            ? entryPrice * (1m + tpPct / 100m)
            : entryPrice * (1m - tpPct / 100m);
        return tp > 0m ? Math.Round(tp, 6) : null;
    }

    /// <summary>
    /// 決定 bracket TP 的距離 %：R:R 模式優先（tpRr&gt;0 → TP 距離 = tpRr × 槓桿感知 SL 距離），
    /// 否則退回固定 tpPct。R:R 模式下 SL 已隨槓桿縮、TP 自動跟著縮，賺賠比恆定。
    /// 回 0 = 不帶 TP。pure static、好測。
    /// </summary>
    internal static decimal ResolveBracketTpPct(decimal tpRr, decimal tpPct, decimal effectiveSlPct)
    {
        if (tpRr > 0m && effectiveSlPct > 0m) return tpRr * effectiveSlPct;
        return tpPct;
    }

    /// <summary>
    /// 把 bracket SL/TP 價格 round 到 symbol 的 price tick——BingX 對精度過長的價格會直接拒單、
    /// SL 靜默掛不上。pricePrecision 來自 SymbolSpecs（dynamic fetch）；null/超範圍 → 退回 6dp（原行為）。
    /// pure static、好測。
    /// </summary>
    internal static decimal RoundPrice(decimal price, int? pricePrecision)
        => (pricePrecision is int p && p >= 0 && p <= 8) ? Math.Round(price, p) : Math.Round(price, 6);

    /// <summary>
    /// 把開倉數量 round DOWN 到 symbol 的 QtyStep——BingX 對精度過長的 qty 也會拒單。
    /// 往下取（floor）而非四捨五入：寧可略小、不要超出 notional / risk 預算。
    /// qtyStep ≤ 0（沒資料）→ 原樣回。pure static、好測。
    /// </summary>
    internal static decimal RoundQtyToStep(decimal qty, decimal qtyStep)
    {
        if (qtyStep <= 0m || qty <= 0m) return qty;
        return Math.Floor(qty / qtyStep) * qtyStep;
    }

    /// <summary>
    /// 算「新 symbol vs 已開倉 symbols」的 30-day daily-return max |correlation|。
    /// fetch 失敗或樣本不足 → maxCorr=0、不擋（保守、避免 false block）。
    /// </summary>
    private async Task<(bool shouldBlock, decimal maxCorr, string? mostCorrelated)> CheckCorrelationCap(
        WatchItem newItem, List<object> perpPositions, CancellationToken ct)
    {
        // 抽出已開倉 symbol（用 reflection、跟現有 anonymous-object pattern 一致）
        var existingSymbols = new List<string>();
        foreach (var pp in perpPositions)
        {
            var sym = pp.GetType().GetProperty("symbol")?.GetValue(pp) as string;
            if (!string.IsNullOrEmpty(sym) && sym != newItem.Symbol) existingSymbols.Add(sym);
        }
        if (existingSymbols.Count == 0) return (false, 0m, null);

        // fetch 新 symbol 的 30-day daily K 線
        var newCloses = await FetchDailyCloses(newItem.Exchange, newItem.Symbol, 35, ct);
        if (newCloses == null || newCloses.Count < 11) return (false, 0m, null);

        // 每個已開倉 symbol 都 fetch、塞 dict 給 CorrelationGuard
        var existingClosesMap = new Dictionary<string, IReadOnlyList<decimal>>();
        foreach (var sym in existingSymbols)
        {
            var closes = await FetchDailyCloses(newItem.Exchange, sym, 35, ct);
            if (closes != null && closes.Count >= 11) existingClosesMap[sym] = closes;
        }
        if (existingClosesMap.Count == 0) return (false, 0m, null);

        var (maxCorr, mostCorrelated) = BrokerCore.Trading.CorrelationGuard.ComputeMaxCorrelation(
            newCloses, existingClosesMap);
        return (maxCorr > _maxCorrelation, maxCorr, mostCorrelated);
    }

    /// <summary>
    /// BingX premiumIndex 公開 endpoint、拉指定 symbol 當前 funding rate（8h period）。
    /// 失敗回 null（保守不擋）；不快取（每 5 min sweep 才用一次、流量很小）。
    /// </summary>
    private async Task<decimal?> FetchFundingRate(string symbol, CancellationToken ct)
    {
        try
        {
            var resp = await _publicHttp.GetAsync(
                $"https://open-api.bingx.com/openApi/swap/v2/quote/premiumIndex?symbol={symbol}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonDocument.Parse(json).RootElement;
            if (!doc.TryGetProperty("code", out var code) || code.GetInt32() != 0) return null;
            if (!doc.TryGetProperty("data", out var data)) return null;
            // BingX 可能回單筆 object 或 array、兩種都接
            var item = data.ValueKind == JsonValueKind.Array ? data[0] : data;
            if (!item.TryGetProperty("lastFundingRate", out var fr)) return null;
            if (fr.ValueKind == JsonValueKind.Number) return fr.GetDecimal();
            if (fr.ValueKind == JsonValueKind.String && decimal.TryParse(fr.GetString(), out var d)) return d;
            return null;
        }
        catch { return null; }
    }

    private async Task<List<decimal>?> FetchDailyCloses(string exchange, string symbol, int limit, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { symbol, exchange, interval = "1d", limit });
        var result = await _dispatcher.DispatchAsync(BuildRequest("quote.ohlcv", "get_bars", payload));
        if (!result.Success) return null;
        var doc = JsonDocument.Parse(result.ResultPayload ?? "{}").RootElement;
        if (!doc.TryGetProperty("bars", out var bars) || bars.ValueKind != JsonValueKind.Array) return null;
        var closes = new List<decimal>();
        foreach (var b in bars.EnumerateArray())
        {
            if (b.TryGetProperty("close", out var c) && c.ValueKind == JsonValueKind.Number)
                closes.Add(c.GetDecimal());
        }
        return closes;
    }

    private static ApprovedRequest BuildRequest(string capabilityId, string route, string payload = "{}")
        => new()
        {
            RequestId = Guid.NewGuid().ToString("N"),
            CapabilityId = capabilityId, Route = route, Payload = payload,
            Scope = "{}", PrincipalId = "system", TaskId = "auto-trader", SessionId = "auto-trader"
        };

    /// <summary>
    /// A2.5b：建 trading.perpetual dispatch 用的 __credentials object。
    /// 找不到（用戶沒設 credential / service 沒注入）回 null、worker 端 fallback env 預設 client。
    /// 找到回 anon object {api_key, api_secret, is_demo}、可直接拼進 payload。
    /// </summary>
    private object? BuildCredentialsObject(string ownerPrincipalId, string exchange)
    {
        if (_credentials == null) return null;
        var dec = _credentials.Resolve(ownerPrincipalId, exchange);
        if (dec == null) return null;
        return new { api_key = dec.ApiKey, api_secret = dec.ApiSecret, is_demo = dec.IsDemo };
    }

    private void AddLog(WatchItem item, string action, string message)
    {
        _tradeLog.Enqueue(new TradeLog
        {
            Symbol = item.Symbol, Exchange = item.Exchange,
            Action = action, Message = message,
        });
        while (_tradeLog.Count > MaxLogEntries) _tradeLog.TryDequeue(out _);

        // ★ 失敗 backoff state：連續錯誤 N 次 → 外層 sweep skip 1 小時、避免狂洗 log + 通知
        var key = $"{item.Exchange}:{item.Symbol}";
        if (action == "error" || action == "blocked")
        {
            _watchFailureState.AddOrUpdate(key,
                _ => new FailureState { ConsecutiveErrors = 1, LastErrorAt = DateTime.UtcNow, LastReason = message },
                (_, fs) =>
                {
                    fs.ConsecutiveErrors++;
                    fs.LastErrorAt = DateTime.UtcNow;
                    fs.LastReason  = message;
                    return fs;
                });
        }
        else if (IsSuccessfulOrderAction(action))
        {
            // 成功下單就清掉之前累積的失敗
            _watchFailureState.TryRemove(key, out _);
        }
    }

    private static bool IsSuccessfulOrderAction(string action)
    {
        if (string.IsNullOrEmpty(action)) return false;
        return action == "buy" || action == "sell" || action == "adjusted"
            || action.StartsWith("open_") || action.StartsWith("close_")
            || action.StartsWith("scale_in_") || action == "protect";
    }

    private class FailureState
    {
        public int ConsecutiveErrors { get; set; }
        public DateTime LastErrorAt  { get; set; }
        public string LastReason     { get; set; } = "";
    }

    private readonly ConcurrentDictionary<string, FailureState> _watchFailureState = new();
    private const int      MaxConsecutiveErrors = 3;
    private static readonly TimeSpan FailureBackoffWindow = TimeSpan.FromHours(1);

    private static string TruncateReason(string reason)
        => reason.Length > 120 ? reason[..120] + "…" : reason;

    // ── Portfolio Scanner Hybrid sweep(2026-05-27 Phase 1 Step B 骨架)──────────────────────────
    //
    // 跟 watch foreach 互補:watches 是固定 (策略, 幣) 契約、scanner 是「策略 + 候選幣池」、
    // 每 cycle 在 universe 內挑訊號最強的 N 個開倉(`scanner_active_legs` 記錄已開部位)。
    //
    // 骨架階段(B.1)只 log:
    //   - 載入 scanner_legs WHERE enabled=1
    //   - 對每個 scanner、跳過已達 max_concurrent 的、log "scanning universe of N symbols"
    //   - 還沒 fetch signal 也沒 dispatch — 留給 B.2 / B.3
    //
    // 設計來源:docs/designs/portfolio-scanner-hybrid.md
    private async Task SweepScannerLegsAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        List<ScannerLegEntry> scanners;
        try
        {
            // BaseOrm Query<T> 用 SQL string、跨 thread 安全(BrokerDb 內部加鎖)
            scanners = _db.Query<ScannerLegEntry>(
                "SELECT * FROM scanner_legs WHERE enabled = 1 ORDER BY id");
        }
        catch (Exception ex)
        {
            // 第一次部署、scanner_legs 表還沒建好 / Migration 未跑完 → 靜默退化、不擋核心腿
            _logger.LogDebug(ex, "SweepScannerLegsAsync: scanner_legs query failed (likely uninitialized)");
            return;
        }

        if (scanners.Count == 0) return;

        foreach (var scanner in scanners)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                // B.4 close pass:先檢查現有 active leg 有沒有反向訊號需要 close
                // 順序重要:close 跑完才釋出 slot、open 才能立刻補位、同 cycle 內換腿
                await CloseScannerActiveLegsAsync(scanner, ct);
                await ProcessScannerAsync(scanner, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AutoTrader scanner {ScannerId} error", scanner.Id);
            }
        }
    }

    private async Task ProcessScannerAsync(ScannerLegEntry scanner, CancellationToken ct)
    {
        // 載入該 scanner 已開 active legs(避免同 scanner 重複開同幣、檢查 max_concurrent)
        // B.4:soft close 設計 — closed_at IS NULL 才算「目前在 active」
        List<ScannerActiveLegEntry> activeLegs;
        try
        {
            activeLegs = _db.Query<ScannerActiveLegEntry>(
                "SELECT * FROM scanner_active_legs WHERE scanner_id = @ScannerId AND closed_at IS NULL",
                new { ScannerId = scanner.Id });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scanner {ScannerId} active legs query failed", scanner.Id);
            return;
        }

        if (activeLegs.Count >= scanner.MaxConcurrent)
        {
            _logger.LogDebug("Scanner {ScannerId} at capacity ({Active}/{Max}), skipping",
                scanner.Id, activeLegs.Count, scanner.MaxConcurrent);
            return;
        }

        // 解析 universe JSON array(由 Phase 3 UI / portfolio.json seed 填入)
        List<string> universe;
        try
        {
            universe = JsonSerializer.Deserialize<List<string>>(scanner.Universe) ?? new();
        }
        catch (JsonException)
        {
            _logger.LogWarning("Scanner {ScannerId} has invalid universe JSON: {Universe}",
                scanner.Id, scanner.Universe);
            return;
        }

        if (universe.Count == 0) return;

        // 排除規則(2026-05-27 P0' fix:Option C):
        //   1. 已被「真錢核心腿」占用的 symbol — scanner 是機會主義、不撞真錢腿
        //      只避 budget_pct > 0 的腿(真錢配重);active=1 但 budget=0 的實驗 watch 不擋
        //      (避免實驗 watch 把整個 alt-coin 池子鎖死、scanner 永遠 candidates=0)
        //   2. 已被本 scanner 開的 symbol(active legs)
        var coreOccupied = _watchList.Values
            .Where(w => w.Active && w.BudgetPct > 0m)
            .Select(w => w.Symbol)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var alreadyOpen = activeLegs.Select(a => a.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = universe
            .Where(sym => !coreOccupied.Contains(sym) && !alreadyOpen.Contains(sym))
            .ToList();

        var slots = scanner.MaxConcurrent - activeLegs.Count;
        // legOpen/Max = 本 leg 自己的開倉/上限(受 MaxConcurrent 強制);coreOcc = 全域真錢 watch 數(跨 leg 共用、僅用於排除已佔 symbol)
        // 注意:舊版 occupied = legOpen + coreOcc 混在一起 = 誤導(看起來像超開倉、其實沒)
        _logger.LogInformation(
            "Scanner {ScannerId} strategy={Strategy} interval={Interval} universe={U} legOpen={LegOpen}/{Max} coreOcc={CoreOcc} candidates={C} slots={Slots} shadow={Shadow}",
            scanner.Id, scanner.Strategy, scanner.Interval, universe.Count,
            alreadyOpen.Count, scanner.MaxConcurrent, coreOccupied.Count, candidates.Count, slots, scanner.Shadow);

        if (candidates.Count == 0 || slots <= 0) return;

        if (!_registry.HasAvailableWorker("quote.ohlcv") || !_registry.HasAvailableWorker("strategy.signal"))
        {
            _logger.LogDebug("Scanner {ScannerId} skipped — quote/strategy worker unavailable", scanner.Id);
            return;
        }

        // B.2:對每個 candidate fetch bars + signal、收集非 hold 且通過 confidence threshold 的進 picks。
        // scanner 用自己的 interval(1d / 1w),不跟核心腿固定 1d 綁定。
        // B.4:同時抓 lastBarClose 給 DispatchScannerLegAsync 當 shadow EntryPrice。
        // C 路線:scanner 每 cycle 預先 fetch 一次 BTC bars,給策略 worker 注入 BtcRegimeFilterStrategy.BtcBarsRef
        // 沒拉到 BTC 也照跑(wrapper 沒注入會 pass-through),不擋核心腿
        JsonElement? refBtcBars = null;
        try
        {
            var btcSym = scanner.Interval == "1w" || scanner.Interval == "1d" ? "BTCUSDT" : "BTCUSDT";
            var btcPayload = JsonSerializer.Serialize(new { symbol = btcSym, interval = scanner.Interval, limit = 200 });
            var btcResult = await _dispatcher.DispatchAsync(BuildRequest("quote.ohlcv", "get_bars", btcPayload));
            if (btcResult.Success)
            {
                var btcDoc = JsonDocument.Parse(btcResult.ResultPayload ?? "{}");
                if (btcDoc.RootElement.TryGetProperty("bars", out var btcArr) && btcArr.GetArrayLength() >= 50)
                    refBtcBars = btcArr.Clone();   // Clone 因為 doc 出函式 scope 後會 dispose
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Scanner {ScannerId} BTC ref bars fetch failed", scanner.Id); }

        var ranked = new List<(string Symbol, string Action, decimal Confidence, string Reason, long SignalBarTs, decimal EntryPrice)>();
        foreach (var sym in candidates)
        {
            if (ct.IsCancellationRequested) return;

            // Step 1: K 線(scanner.Interval、limit 200 同核心腿 ProcessSymbolAsync)
            var barsPayload = JsonSerializer.Serialize(new { symbol = sym, interval = scanner.Interval, limit = 200 });
            // 2026-05-27 改用 get_bars_funding:bars 帶 funding_rate 欄、funding_momentum_ls 等策略才能用
            // 其他策略無 funding 邏輯、額外欄不影響
            var barsResult = await _dispatcher.DispatchAsync(BuildRequest("quote.ohlcv", "get_bars_funding", barsPayload));
            if (!barsResult.Success) continue;

            JsonElement barsArr;
            long latestBarTs = 0;
            decimal lastBarClose = 0m;
            try
            {
                var barsDoc = JsonDocument.Parse(barsResult.ResultPayload ?? "{}");
                if (!barsDoc.RootElement.TryGetProperty("bars", out barsArr) || barsArr.GetArrayLength() < 30) continue;
                // 最後一根 K 線時間戳當 signal_bar_ts、給冪等鎖用。
                // quote.ohlcv 不同 exchange / interval 回傳的 open_time 可能是 Number(ms epoch)或 String(ISO/數字字串),都接受。
                var lastBar = barsArr[barsArr.GetArrayLength() - 1];
                if (lastBar.TryGetProperty("open_time", out var ot))
                {
                    if (ot.ValueKind == JsonValueKind.Number) latestBarTs = ot.GetInt64();
                    else if (ot.ValueKind == JsonValueKind.String)
                    {
                        var s = ot.GetString() ?? "";
                        if (long.TryParse(s, out var asMs)) latestBarTs = asMs;
                        else if (DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var asDt))
                            latestBarTs = new DateTimeOffset(asDt, TimeSpan.Zero).ToUnixTimeMilliseconds();
                    }
                }
                // B.4:取 close 當 shadow EntryPrice(real dispatch 階段會被實際成交價覆蓋)
                if (lastBar.TryGetProperty("close", out var cl))
                {
                    if (cl.ValueKind == JsonValueKind.Number) lastBarClose = cl.GetDecimal();
                    else if (cl.ValueKind == JsonValueKind.String && decimal.TryParse(cl.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dc)) lastBarClose = dc;
                }
            }
            catch (JsonException) { continue; }
            catch (InvalidOperationException) { continue; }
            catch (FormatException) { continue; }

            // Step 2: 策略訊號
            var signalPayloadDict = new Dictionary<string, object?>
            {
                ["strategy"] = scanner.Strategy,
                ["symbol"]   = sym,
                ["exchange"] = string.IsNullOrEmpty(scanner.Exchange) ? "binance" : scanner.Exchange,   // 2026-05-29 多市場
                ["interval"] = scanner.Interval,
                ["bars"]     = barsArr,
            };
            // C 路線:注入 BTC ref bars(給 BtcRegimeFilterStrategy 用、其他策略無視)
            if (refBtcBars.HasValue) signalPayloadDict["ref_btc_bars"] = refBtcBars.Value;
            var signalResult = await _dispatcher.DispatchAsync(
                BuildRequest("strategy.signal", "evaluate", JsonSerializer.Serialize(signalPayloadDict)));
            if (!signalResult.Success) continue;

            string action; decimal confidence; string reason;
            try
            {
                var sig = JsonDocument.Parse(signalResult.ResultPayload ?? "{}").RootElement;
                action     = sig.TryGetProperty("action",     out var aEl) ? aEl.GetString() ?? "hold" : "hold";
                confidence = sig.TryGetProperty("confidence", out var cEl) ? cEl.GetDecimal() : 0m;
                reason     = sig.TryGetProperty("reason",     out var rEl) ? rEl.GetString() ?? "" : "";
            }
            catch (JsonException) { continue; }

            // 排除 hold + 低信心(走 _minConfidence、跟核心腿一致)
            if (action == "hold" || confidence < _minConfidence) continue;

            ranked.Add((sym, action, confidence, reason, latestBarTs, lastBarClose));
        }

        if (ranked.Count == 0)
        {
            _logger.LogDebug("Scanner {ScannerId} no actionable signals (universe scanned, all hold/low-conf)", scanner.Id);
            return;
        }

        // 按 confidence 降序、取前 slots 個。confidence 同分時 symbol 字母序穩定。
        var picks = ranked
            .OrderByDescending(r => r.Confidence)
            .ThenBy(r => r.Symbol, StringComparer.Ordinal)
            .Take(slots)
            .ToList();

        var pickSummary = string.Join(", ", picks.Select(p => $"{p.Symbol}@{p.Action}/{p.Confidence:0.00}"));
        _logger.LogInformation("Scanner {ScannerId} picks ({N}/{Slots}): {Picks}",
            scanner.Id, picks.Count, slots, pickSummary);

        // B.3 dispatch:逐一檢冪等鎖、shadow 直接 persist(只記不下單)、real 路徑暫保留給 B.3b。
        foreach (var pick in picks)
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                await DispatchScannerLegAsync(scanner, pick.Symbol, pick.Action, pick.Confidence, pick.SignalBarTs, pick.EntryPrice, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scanner {ScannerId} dispatch failed for {Symbol}", scanner.Id, pick.Symbol);
            }
        }
    }

    private async Task DispatchScannerLegAsync(
        ScannerLegEntry scanner, string symbol, string action, decimal confidence, long signalBarTs,
        decimal entryPriceProxy, CancellationToken ct)
    {
        // 冪等鎖:同 (scanner_id, symbol, signal_bar_ts) 不重複開。
        // signal_bar_ts 是 candidate 抓到的最後一根 K 線開盤時間(unix ms)、
        // 同 cycle 重跑、或 broker 重啟後同 bar 都不會重開。
        if (signalBarTs > 0)
        {
            int existing;
            try
            {
                existing = _db.Scalar<int>(
                    "SELECT COUNT(*) FROM scanner_active_legs WHERE scanner_id = @ScannerId AND symbol = @Symbol AND signal_bar_ts = @SignalBarTs",
                    new { ScannerId = scanner.Id, Symbol = symbol, SignalBarTs = signalBarTs });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scanner {ScannerId} idempotency check failed for {Symbol}", scanner.Id, symbol);
                return;
            }
            if (existing > 0)
            {
                _logger.LogDebug("Scanner {ScannerId} {Symbol} bar {Ts} already dispatched, skipping",
                    scanner.Id, symbol, signalBarTs);
                return;
            }
        }

        // 構造 active leg(無論 shadow 或 real 都持久化、shadow=true 標記)
        var side = action == "sell" ? "short" : "long";
        var leg = new ScannerActiveLegEntry
        {
            Id              = $"{scanner.Id}:{symbol}:{signalBarTs}",
            ScannerId       = scanner.Id,
            Symbol          = symbol,
            Exchange        = string.IsNullOrEmpty(scanner.Exchange) ? "binance" : scanner.Exchange,   // 2026-05-29 多市場
            Side            = side,
            EntryPrice      = entryPriceProxy,    // B.4:shadow 用 bar close 模擬;real dispatch 階段會被實際成交價覆蓋
            PeakMark        = entryPriceProxy,    // 起算 peak = entry,close pass 後續更新
            SignalBarTs     = signalBarTs,
            EntrySignal     = $"{action}@{scanner.Interval}",
            EntryConfidence = confidence,
            Shadow          = scanner.Shadow,
            OpenedAt        = DateTime.UtcNow,
            UpdatedAt       = DateTime.UtcNow,
            OwnerPrincipalId = scanner.OwnerPrincipalId,
        };

        if (scanner.Shadow)
        {
            // Shadow 模式:只 persist active leg(標 shadow=true)、log "would open"、不下任何真單。
            // 後續 cycle 反向訊號出來時、scanner close 路徑(B.4)會把這條移走、累積 shadow PnL 給 portfolio review 看。
            try
            {
                _db.Insert(leg);
                _logger.LogInformation("[SHADOW] Scanner {ScannerId} would open {Side} {Symbol} (conf={Conf:0.00}, bar={Bar}) — recorded, no real order",
                    scanner.Id, side, symbol, confidence, signalBarTs);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scanner {ScannerId} failed to insert shadow leg for {Symbol}", scanner.Id, symbol);
            }
            return;
        }

        // Real 模式:暫保留給 B.3b。
        // 該路徑需要:
        //   1. 走核心腿 ProcessSymbolAsync 同款 risk gate(circuit breaker、portfolio DD、approval flow)
        //   2. PlaceSpotOrderAsync / PlacePerpOrderForSignalAsync 派單
        //   3. 真實成交價填入 leg.EntryPrice / leg.PeakMark / leg.SlPrice
        //   4. Insert leg(成功才插、避免 ghost leg)
        // 因為涉及真錢、且 4 週 shadow 紀律期還沒過、本 commit 不開放 real dispatch:
        _logger.LogWarning("Scanner {ScannerId} non-shadow dispatch for {Symbol} requested — NOT IMPLEMENTED yet (B.3b)。請保持 shadow=true 直到 4 週 shadow paper trade 達標、再實作 real dispatch。",
            scanner.Id, symbol);
        await Task.CompletedTask;
    }

    // B.4 close-side lifecycle(2026-05-27)
    // 每 cycle 在 ProcessScannerAsync 開新腿之前跑一次:
    //   - 查該 scanner 所有 closed_at IS NULL 的 leg
    //   - 對每條 leg 拉同 scanner.Strategy × leg.Symbol × scanner.Interval 的最新訊號
    //   - 若訊號反向(leg.Side="long" 收 "sell"、或 "short" 收 "buy")→ 關腿
    //   - 計算 realized_pnl_pct(含 0.16% 約算 round-trip 成本)、UPDATE 該 leg
    //   - log "[SHADOW] closed ... pnl=±X%"、給 shadow 4 週評估累加用
    //
    // 安全:
    //   - 只在 shadow=true 路徑跑。real 路徑(B.3b 未實作)會由 real exit 流程處理
    //   - 找不到訊號 / 資料不足 / 訊號同向 → leg 保留不動
    //   - update 失敗不 throw、記 warning 不影響其他 leg
    //
    // TP/SL 觸發:Phase 1 不處理。等策略明確 emit StopPrice/TargetPrice 進 leg 後 Phase 2 再加。
    // Funding / 隔夜利息:Phase 1 不入 PnL。P5 真實 funding rate 接入後再說。
    private async Task CloseScannerActiveLegsAsync(ScannerLegEntry scanner, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        List<ScannerActiveLegEntry> activeLegs;
        try
        {
            activeLegs = _db.Query<ScannerActiveLegEntry>(
                "SELECT * FROM scanner_active_legs WHERE scanner_id = @ScannerId AND closed_at IS NULL",
                new { ScannerId = scanner.Id });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CloseScannerActiveLegsAsync: active legs query failed for {ScannerId}", scanner.Id);
            return;
        }

        if (activeLegs.Count == 0) return;
        if (!_registry.HasAvailableWorker("quote.ohlcv") || !_registry.HasAvailableWorker("strategy.signal"))
        {
            _logger.LogDebug("Scanner {ScannerId} close pass skipped — workers unavailable", scanner.Id);
            return;
        }

        foreach (var leg in activeLegs)
        {
            if (ct.IsCancellationRequested) return;

            try
            {
                // Step 1: 拉 bars
                var barsPayload = JsonSerializer.Serialize(new { symbol = leg.Symbol, interval = scanner.Interval, limit = 200 });
                var barsResult = await _dispatcher.DispatchAsync(BuildRequest("quote.ohlcv", "get_bars", barsPayload));
                if (!barsResult.Success) continue;

                JsonElement barsArr;
                decimal lastBarClose = 0m;
                try
                {
                    var barsDoc = JsonDocument.Parse(barsResult.ResultPayload ?? "{}");
                    if (!barsDoc.RootElement.TryGetProperty("bars", out barsArr) || barsArr.GetArrayLength() < 30) continue;
                    var lastBar = barsArr[barsArr.GetArrayLength() - 1];
                    if (lastBar.TryGetProperty("close", out var cl))
                    {
                        if (cl.ValueKind == JsonValueKind.Number) lastBarClose = cl.GetDecimal();
                        else if (cl.ValueKind == JsonValueKind.String && decimal.TryParse(cl.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dc)) lastBarClose = dc;
                    }
                }
                catch (Exception) { continue; }

                if (lastBarClose <= 0m) continue;

                // Step 2: 拉訊號
                var signalPayloadDict = new Dictionary<string, object?>
                {
                    ["strategy"] = scanner.Strategy,
                    ["symbol"]   = leg.Symbol,
                    ["exchange"] = leg.Exchange,
                    ["interval"] = scanner.Interval,
                    ["bars"]     = barsArr,
                };
                var signalResult = await _dispatcher.DispatchAsync(
                    BuildRequest("strategy.signal", "evaluate", JsonSerializer.Serialize(signalPayloadDict)));
                if (!signalResult.Success) continue;

                string action;
                try
                {
                    var sig = JsonDocument.Parse(signalResult.ResultPayload ?? "{}").RootElement;
                    action = sig.TryGetProperty("action", out var aEl) ? aEl.GetString() ?? "hold" : "hold";
                }
                catch (Exception) { continue; }

                // Step 3: 反向訊號才關腿。同向或 hold 都不動。
                var oppositeOf = leg.Side == "long" ? "sell" : "buy";
                if (action != oppositeOf) continue;

                // Step 4: 計 PnL(含 round-trip 成本 ≈ 0.16% = (0.05 commission + 0.03 slippage) × 2 邊)
                decimal entry = leg.EntryPrice;
                if (entry <= 0m) continue;   // 沒 entry 沒法算、跳過(理論不該發生、B.4.2 已修)
                decimal grossPct = leg.Side == "long"
                    ? (lastBarClose / entry - 1m) * 100m
                    : (entry / lastBarClose - 1m) * 100m;
                decimal pnlPct = grossPct - 0.16m;

                // Step 5: UPDATE leg
                var closedAt = DateTime.UtcNow;
                try
                {
                    _db.Execute(
                        "UPDATE scanner_active_legs SET closed_at = @ClosedAt, exit_price = @ExitPrice, realized_pnl_pct = @PnlPct, close_reason = 'reverse', updated_at = @ClosedAt WHERE id = @Id",
                        new { Id = leg.Id, ClosedAt = closedAt, ExitPrice = lastBarClose, PnlPct = pnlPct });
                    _logger.LogInformation("[SHADOW] Scanner {ScannerId} closed {Side} {Symbol} — entry={Entry:0.0000} exit={Exit:0.0000} pnl={Pnl:+0.00;-0.00}% (reverse signal {Action})",
                        scanner.Id, leg.Side, leg.Symbol, entry, lastBarClose, pnlPct, action);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Scanner {ScannerId} failed to update closed leg {LegId}", scanner.Id, leg.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scanner {ScannerId} close pass error for leg {LegId}", scanner.Id, leg.Id);
            }
        }
    }
}

// ── Models ──────────────────────────────────────────────────────────

public class WatchItem
{
    public string Symbol   { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Strategy { get; set; } = "composite";
    public decimal Quantity { get; set; } = 1;
    public bool Active     { get; set; } = true;
    public string? LastSignal    { get; set; }
    public decimal LastConfidence { get; set; }
    public DateTime? LastCheck    { get; set; }
    /// <summary>"spot" / "perp_long_only" / "perp_both" — 預設 spot 保持既有行為。</summary>
    public string Mode     { get; set; } = "spot";
    /// <summary>perpetual 模式開倉用槓桿。spot 模式忽略。預設 5x。</summary>
    public int Leverage    { get; set; } = 5;
    /// <summary>Phase A2：擁有者 principal_id。admin 看全部、user 看自己這個 == 自己 principal 的。</summary>
    public string OwnerPrincipalId { get; set; } = "prn_dashboard";

    /// <summary>
    /// HTF 大週期確認週期、例如 "4h"、"1d"、"1w"。
    /// 設定後 sweep 會額外 fetch 對應級別 K 線、傳給 strategy.signal 做大週期方向確認。
    /// 預設 null = 不做 HTF、保留既有行為。
    /// </summary>
    public string? HtfInterval { get; set; }

    /// <summary>
    /// Shadow 模式:照常評估訊號 + 風控 + log,但「絕不下真單」。新策略上線前先 shadow 對帳用。
    /// 預設 false = 真交易。必須持久化(見 AutoTradeWatchEntry.Shadow),否則重啟會變回真交易。
    /// </summary>
    public bool Shadow { get; set; } = false;

    /// <summary>資金預算制:本 watch 開倉名目佔餘額 %。&gt;0 覆蓋全域 exposure_pct(多倉各配額度、不先搶先贏);0=用全域。</summary>
    public decimal BudgetPct { get; set; } = 0m;

    /// <summary>2026-06-04:用 strategy emit 的結構性停損(諧波 PRZ 失效價)當初始 SL,而非通用固定 %。
    /// 預設 false。仍會過 LeverageAwareSlPct 收緊到強平距離內。</summary>
    public bool UseSignalSl { get; set; } = false;

    /// <summary>執行期暫存:本輪 strategy 訊號 emit 的 StopPrice(由 ProcessSymbol 解析、傳給開倉路徑)。
    /// 非持久化、每輪覆寫;只在 UseSignalSl=true 時填。</summary>
    public decimal? PendingSignalSl { get; set; }
}

public class TradeLog
{
    public string Symbol   { get; set; } = "";
    public string Exchange { get; set; } = "";
    public string Action   { get; set; } = "";
    public string Message  { get; set; } = "";
    public DateTime Time   { get; set; } = DateTime.UtcNow;
}
