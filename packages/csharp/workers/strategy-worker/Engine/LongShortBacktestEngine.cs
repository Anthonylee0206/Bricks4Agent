using StrategyWorker.Models;

namespace StrategyWorker.Engine;

/// <summary>
/// 多空(long-short)回測引擎 —— Benson 的 BacktestEngine 是 long-only(buy 開多、sell 只平倉),
/// 這支獨立新增、不動原檔,把訊號語意擴成「多空反手(stop-and-reverse)」:
///   buy(信心≥0.6)  → 目標做多:空單先回補再開多;空手開多;已多維持
///   sell(信心≥0.6) → 目標做空:多單先平再開空;空手開空;已空維持
///   hold / 低信心    → 維持當前部位
///
/// 現金記帳對多空通用:equity = cash + position×price(position 帶正負號)。
///   開多:cash −= qty×price;開空:cash += qty×price(收到賣出價金);兩邊都再扣手續費。
///   → 價漲時多單 equity 升、空單 equity 降,符合直覺。無槓桿(名目 ≤ notionalPct×equity)。
///
/// 無 lookahead:逐根只餵 bars[0..i];部位用「當根收盤」進出。tradeStartIndex 給 walk-forward OOS 用
/// (前段只當指標 warmup、不計交易/績效)。
///
/// SL / TP(2026-05-26 H16):訊號帶 StopPrice / TargetPrice 時、盤中觸價即平倉;同根都觸發按 SL 先。
/// 不發 stop/target 的策略不受影響(active*Price=0 整段 skip)。
/// </summary>
public static class LongShortBacktestEngine
{
    public static BacktestEngine.BacktestResult Run(
        IStrategy strategy,
        List<BarData> bars,
        StrategyConfig config,
        decimal initialCash = 100_000,
        decimal commission = 0.001m,
        int tradeStartIndex = 0,
        decimal slippagePct = 0m,
        decimal notionalPct = 0.95m,   // 每次開倉名目佔當前 equity 比例(無槓桿)
        bool confidenceSizing = false, // true:名目再 × signal.Confidence(淨加權 ensemble 用來「分歧縮量」)
        decimal atrTrailMultiplier = 0m, // H18:>0 啟用 ATR trailing SL(每根 ratchet activeStopPrice、不放鬆)
        int atrPeriod = 14,            // ATR 視窗
        decimal defaultInitialSlPct = 0m, // H18 補正:策略未 emit StopPrice 但啟用 trail 時、開倉用此 % 做初始 SL(讓 trail 有 base 可 ratchet)
        decimal peakTrailTriggerPct = 0m, // H18-live-align:peak-based trail 啟動門檻(peak gain ≥ X% 才啟動、跟 AutoTraderService.TrailingTriggerPct 對齊)
        decimal peakTrailDistancePct = 0m, // peak-based trail 距離(SL = peak × (1 ∓ distance%/100)、跟 AutoTraderService.TrailingDistancePct 對齊)
        decimal beTriggerPct = 0m,        // 2026-06-04 live-align:peak gain ≥ X% → SL 移到 entry ± buffer(BE-move、跟 AutoTraderService.BreakevenTriggerPct 對齊)。0=skip
        decimal beBufferPct = 0m,         // BE 上方 buffer %(SL = entry × (1 ± buffer/100)、跟 BreakevenBufferPct 對齊)
        bool applyFunding = false,        // D 路線(2026-05-27):永續 funding 計入 PnL。需 bars[i].FundingRate 已注入(用 ToolsShared.FundingCache.InjectInto)
                                          // 約定:bar.FundingRate = 該根 bar 持有期間的【累計】 funding rate(SUM、不是 avg)
                                          // 多單:cash -= position × close × fundingRate(正 funding=付、負=收)
                                          // 空單:cash -= position × close × fundingRate(position 已帶負號、正 funding=收、負=付)
                                          // 兩邊用同公式、靠 position 符號自動處理 short sign flip
        bool volTargetSizing = false,     // Q1.2 vol-targeting:開倉名目再 × VolTargetSizer scalar(target/realized vol、clamp 0.3~2.0)
                                          // 跟 confidenceSizing 相乘相容。用「進場當下、僅看 t 之前 closes」算 realized vol(無 lookahead)。
        decimal volTargetAnnual = 0.60m,  // 目標年化波動(預設 60% ≈ BTC 長期平均);realized 高於它→縮、低於它→放
        int volTargetLookback = 30,       // realized vol 回看窗(日)
        decimal volTargetMaxScalar = 2.0m) // scalar 上限。=1.0 → 「只縮不放」de-risk-only(永不加槓桿、合存活紀律)
    {
        var costRate = commission + slippagePct;
        var result = new BacktestEngine.BacktestResult
        {
            Strategy = strategy.Name,
            Symbol = config.Symbol,
            InitialCash = initialCash,
            TotalBars = bars.Count,
            StartDate = bars.FirstOrDefault()?.OpenTime ?? DateTime.MinValue,
            EndDate = bars.LastOrDefault()?.OpenTime ?? DateTime.MinValue,
        };
        if (bars.Count < 50) return result;

        decimal cash = initialCash;
        decimal position = 0m;       // 帶正負號:>0 多、<0 空
        decimal entryPrice = 0m;
        int entryIndex = -1;
        decimal entryConfidence = 0m;  // 進場當下 signal.Confidence(confidence 校準診斷用)
        DateTime entryDate = DateTime.MinValue;
        decimal activeStopPrice = 0m;    // H2-Fib SL 支援:開倉時鎖定的停損價(0=不啟用、靠反向訊號平倉)
        decimal activeTargetPrice = 0m;  // H16 TP 支援:開倉時鎖定的獲利目標(0=不啟用)
        decimal peakPrice = 0m;          // H18 peak-trail:position 期間的最佳價(long=最高、short=最低、跟 AutoTraderService.PeakPrice 對齊)
        decimal activePartialTarget = 0m; decimal partialFraction = 0m; bool partialDone = false;  // Phase 2 部分出場(scale-out)
        decimal peakEquity = initialCash, maxDrawdown = 0m, prevEquity = initialCash;
        var dailyReturns = new List<decimal>();

        int lookback = Math.Max(Math.Max(config.SmaSlow + 5, 50), tradeStartIndex);

        void CloseAt(int i, decimal px)
        {
            if (position == 0m) return;
            decimal qty = Math.Abs(position);
            decimal exitNotional = qty * px;
            cash += position * px;                       // 多:賣出回現金;空:回補付現(position 負 → cash 減)
            cash -= exitNotional * costRate;
            decimal pnl = position * (px - entryPrice)
                          - qty * entryPrice * costRate
                          - exitNotional * costRate;
            decimal pnlPct = entryPrice > 0m
                ? (position > 0m ? (px - entryPrice) : (entryPrice - px)) / entryPrice * 100m
                : 0m;
            result.Trades.Add(new BacktestEngine.BacktestTrade
            {
                Side = position > 0m ? "long" : "short",
                EntryDate = entryDate, EntryPrice = entryPrice,
                ExitDate = bars[i].OpenTime, ExitPrice = px,
                Quantity = qty, Pnl = Math.Round(pnl, 2), PnlPct = Math.Round(pnlPct, 2),
                HoldBars = entryIndex >= 0 ? i - entryIndex : 0,
                EntryConfidence = entryConfidence,
            });
            position = 0m; entryPrice = 0m; entryIndex = -1; entryConfidence = 0m; activeStopPrice = 0m; activeTargetPrice = 0m; peakPrice = 0m;
            activePartialTarget = 0m; partialFraction = 0m; partialDone = false;
        }

        // Phase 2 部分出場:平掉 frac 比例、其餘續抱(entryPrice 不變)。記一筆 trade、PnL 照算。
        void PartialCloseAt(int i, decimal px, decimal frac)
        {
            if (position == 0m || frac <= 0m || frac >= 1m) return;
            decimal closeQty = Math.Abs(position) * frac;
            decimal closedPos = (position > 0m ? 1m : -1m) * closeQty;
            decimal exitNotional = closeQty * px;
            cash += closedPos * px;
            cash -= exitNotional * costRate;
            decimal pnl = closedPos * (px - entryPrice) - closeQty * entryPrice * costRate - exitNotional * costRate;
            decimal pnlPct = entryPrice > 0m
                ? (position > 0m ? (px - entryPrice) : (entryPrice - px)) / entryPrice * 100m : 0m;
            result.Trades.Add(new BacktestEngine.BacktestTrade
            {
                Side = position > 0m ? "long" : "short",
                EntryDate = entryDate, EntryPrice = entryPrice,
                ExitDate = bars[i].OpenTime, ExitPrice = px,
                Quantity = closeQty, Pnl = Math.Round(pnl, 2), PnlPct = Math.Round(pnlPct, 2),
                HoldBars = entryIndex >= 0 ? i - entryIndex : 0, EntryConfidence = entryConfidence,
            });
            position -= closedPos;      // 剩 (1-frac) 部位續抱
            partialDone = true;
        }

        void OpenAt(int i, decimal px, int dir, decimal sizeScale, decimal stop, decimal target, decimal confidence,
            decimal partialTarget, decimal partialFrac)
        {
            decimal eqNow = cash;                        // 已平倉、position=0 → equity=cash
            decimal notional = eqNow * notionalPct * sizeScale;
            if (notional <= 0m) return;
            decimal qty = notional / px;
            position = dir * qty;
            cash -= position * px;                        // 多:扣現;空:收現金
            cash -= notional * costRate;
            entryPrice = px; entryIndex = i; entryDate = bars[i].OpenTime; entryConfidence = confidence;
            activeStopPrice = stop;                       // H2-Fib SL:訊號未帶 stop 時=0、不啟用
            activeTargetPrice = target;                   // H16 TP:訊號未帶 target 時=0、不啟用
            peakPrice = px;                               // H18 peak-trail:從進場價開始追蹤
            activePartialTarget = partialTarget;          // Phase 2:訊號未帶=0、不啟用
            partialFraction = (partialFrac > 0m && partialFrac < 1m) ? partialFrac : 0.5m;
            partialDone = false;
        }

        for (int i = lookback; i < bars.Count; i++)
        {
            var window = bars.GetRange(0, i + 1);
            var signal = strategy.Evaluate(window, config);
            var price = bars[i].Close;

            // ── 盤中觸發 SL / TP 即平倉(策略不發 stop/target 時 active*Price=0、整段 skip)──
            // 同根同時觸 SL 與 TP 時 SL 先(保守、悲觀假設、市場標準做法)。
            bool stoppedOutThisBar = false;
            if (position != 0m && activeStopPrice > 0m)
            {
                bool slHit = position > 0m
                    ? bars[i].Low  <= activeStopPrice    // long: 跌破 SL
                    : bars[i].High >= activeStopPrice;   // short: 漲破 SL
                if (slHit)
                {
                    CloseAt(i, activeStopPrice);          // 在 SL 價位出場(保守、會清 activeStopPrice)
                    stoppedOutThisBar = true;
                }
            }
            // Phase 2 部分出場:未停損、有 partial target、還沒部分平過 → 觸價平 fraction、其餘續抱到 TP/SL
            if (!stoppedOutThisBar && position != 0m && activePartialTarget > 0m && !partialDone)
            {
                bool ptHit = position > 0m ? bars[i].High >= activePartialTarget : bars[i].Low <= activePartialTarget;
                if (ptHit) PartialCloseAt(i, activePartialTarget, partialFraction);
            }
            // H16 TP:SL 未觸發才檢 TP(同根 SL+TP 都進範圍 → 按 SL 出、不雙重歸因)
            if (!stoppedOutThisBar && position != 0m && activeTargetPrice > 0m)
            {
                bool tpHit = position > 0m
                    ? bars[i].High >= activeTargetPrice  // long: 漲破 TP
                    : bars[i].Low  <= activeTargetPrice; // short: 跌破 TP
                if (tpHit)
                {
                    CloseAt(i, activeTargetPrice);        // 在 TP 價位出場
                    stoppedOutThisBar = true;             // 與 SL 共用旗標:本根不再因訊號開新倉
                }
            }

            // D 路線:funding 計入 cash(在 equity 計算前先扣)。
            // 用 position(已帶符號)× close × fundingRate、多空通用、不需分支。
            // 開倉當根不收(從進場後第一根開始;對齊 BacktestEngine 邏輯、避免 entry-bar double charge)。
            if (applyFunding && position != 0m && i > entryIndex && bars[i].FundingRate is decimal fr && fr != 0m)
            {
                cash -= position * price * fr;
            }

            var equity = cash + position * price;
            result.EquityCurve.Add(new BacktestEngine.EquityPoint { Date = bars[i].OpenTime, Value = equity });
            if (prevEquity > 0m) dailyReturns.Add((equity - prevEquity) / prevEquity);
            prevEquity = equity;
            if (equity > peakEquity) peakEquity = equity;
            var dd = peakEquity - equity;
            if (dd > maxDrawdown) maxDrawdown = dd;

            if (stoppedOutThisBar) continue;              // 本根已 SL 出場、不再因訊號開新倉

            int cur = position > 0m ? 1 : position < 0m ? -1 : 0;
            int desired = cur;
            if (signal.Action == "buy" && signal.Confidence >= 0.6m) desired = 1;
            else if (signal.Action == "sell" && signal.Confidence >= 0.6m) desired = -1;
            // hold 或低信心 → desired = cur(維持)

            if (desired != cur)
            {
                CloseAt(i, price);
                if (desired != 0)
                {
                    decimal sizeScale = confidenceSizing ? Math.Clamp(signal.Confidence, 0m, 1m) : 1m;
                    // Q1.2 vol-targeting:用進場當下、僅 t 之前的 closes 算 realized vol → scalar(高 vol 縮/低 vol 放)。
                    // 與 confidenceSizing 相乘相容;無 lookahead(只取 bars[0..i])。
                    if (volTargetSizing)
                    {
                        var closesSoFar = bars.GetRange(0, i + 1).Select(b => b.Close).ToList();
                        var realizedVol = VolTargetSizer.AnnualizedRealizedVol(closesSoFar, volTargetLookback);
                        sizeScale *= VolTargetSizer.ComputeScalar(realizedVol, volTargetAnnual, 0.3m, volTargetMaxScalar);
                    }
                    // H18 補正:策略未 emit StopPrice 但 caller 啟用 trail + 給 defaultInitialSlPct → bootstrap SL,
                    // 讓 trail 有 base 可 ratchet(否則 trail 永遠 skip、對 trend 策略 no-op)。
                    decimal effectiveStop = signal.StopPrice ?? 0m;
                    // 策略未 emit StopPrice 但 caller 給 defaultInitialSlPct → 設固定初始 SL。
                    // (原只在 trail 啟用時 bootstrap;2026-05-29 放寬:固定止損也適用、用於模擬 live 止損機制做存活測試。
                    //  向後相容:defaultInitialSlPct 預設 0 → 不設 SL、現有行為不變。)
                    if (effectiveStop == 0m && defaultInitialSlPct > 0m)
                    {
                        effectiveStop = desired > 0
                            ? price * (1m - defaultInitialSlPct / 100m)   // long:entry × (1 − pct%)
                            : price * (1m + defaultInitialSlPct / 100m); // short:entry × (1 + pct%)
                    }
                    OpenAt(i, price, desired, sizeScale, effectiveStop, signal.TargetPrice ?? 0m, signal.Confidence,
                        signal.PartialTargetPrice ?? 0m, signal.PartialExitFraction ?? 0m);
                }
            }

            // H18:ATR trailing SL — 每根用 close − N×ATR(long)/ close + N×ATR(short)更新 activeStopPrice。
            // 只 ratchet 往有利方向(max for long、min for short),從不放鬆;trail 從 next bar 才生效(check-then-trail)。
            // 預設 atrTrailMultiplier=0 → 整段 skip,向後相容、不影響既有策略。
            if (atrTrailMultiplier > 0m && position != 0m && activeStopPrice > 0m)
            {
                var atr = ComputeAtrAt(bars, i, atrPeriod);
                if (atr > 0m)
                {
                    var trailedSl = position > 0m
                        ? bars[i].Close - atrTrailMultiplier * atr
                        : bars[i].Close + atrTrailMultiplier * atr;
                    activeStopPrice = position > 0m
                        ? Math.Max(activeStopPrice, trailedSl)
                        : Math.Min(activeStopPrice, trailedSl);
                }
            }

            // 2026-06-04 live-align:BE-move — 跟 AutoTraderService 同邏輯。peak gain 達 beTriggerPct → SL 移到 entry ± buffer。
            // 只 ratchet 往有利方向、從不放鬆;預設 beTriggerPct=0 → skip。放 trailing 前(trailing 觸發門檻通常更高、會接手往上)。
            if (beTriggerPct > 0m && position != 0m && activeStopPrice > 0m && entryPrice > 0m)
            {
                peakPrice = position > 0m
                    ? Math.Max(peakPrice, bars[i].High)
                    : (peakPrice == 0m ? bars[i].Low : Math.Min(peakPrice, bars[i].Low));
                decimal bePeakPct = position > 0m
                    ? (peakPrice - entryPrice) / entryPrice * 100m
                    : (entryPrice - peakPrice) / entryPrice * 100m;
                if (bePeakPct >= beTriggerPct)
                {
                    var beSl = position > 0m
                        ? entryPrice * (1m + beBufferPct / 100m)
                        : entryPrice * (1m - beBufferPct / 100m);
                    activeStopPrice = position > 0m
                        ? Math.Max(activeStopPrice, beSl)
                        : Math.Min(activeStopPrice, beSl);
                }
            }

            // H18 live-align:Peak-based trailing — 跟 AutoTraderService 同邏輯。
            // peak 達 trigger 後、SL = peak × (1 ∓ distance%);只 ratchet。預設 peakTrailTriggerPct=0 → skip。
            if (peakTrailTriggerPct > 0m && peakTrailDistancePct > 0m && position != 0m && activeStopPrice > 0m && entryPrice > 0m)
            {
                // 更新 peakPrice(long 用 High、short 用 Low)
                peakPrice = position > 0m
                    ? Math.Max(peakPrice, bars[i].High)
                    : (peakPrice == 0m ? bars[i].Low : Math.Min(peakPrice, bars[i].Low));

                // peakPct = 從 entry 出發的最佳 unrealized %
                decimal peakPct = position > 0m
                    ? (peakPrice - entryPrice) / entryPrice * 100m
                    : (entryPrice - peakPrice) / entryPrice * 100m;

                if (peakPct >= peakTrailTriggerPct)
                {
                    var trailedSl = position > 0m
                        ? peakPrice * (1m - peakTrailDistancePct / 100m)
                        : peakPrice * (1m + peakTrailDistancePct / 100m);
                    activeStopPrice = position > 0m
                        ? Math.Max(activeStopPrice, trailedSl)
                        : Math.Min(activeStopPrice, trailedSl);
                }
            }
        }

        if (position != 0m) CloseAt(bars.Count - 1, bars[^1].Close);

        result.FinalValue = cash;
        result.TotalReturn = Math.Round(cash - initialCash, 2);
        result.TotalReturnPct = Math.Round((cash - initialCash) / initialCash * 100m, 2);
        result.TotalTrades = result.Trades.Count;
        result.WinTrades = result.Trades.Count(t => t.Pnl > 0);
        result.LoseTrades = result.Trades.Count(t => t.Pnl <= 0);
        result.WinRate = result.TotalTrades > 0 ? Math.Round((decimal)result.WinTrades / result.TotalTrades * 100m, 1) : 0m;
        result.MaxDrawdown = Math.Round(maxDrawdown, 2);
        result.MaxDrawdownPct = peakEquity > 0m ? Math.Round(maxDrawdown / peakEquity * 100m, 2) : 0m;
        var wins = result.Trades.Where(t => t.Pnl > 0).ToList();
        var losses = result.Trades.Where(t => t.Pnl <= 0).ToList();
        result.AvgWin = wins.Count > 0 ? Math.Round(wins.Average(t => t.Pnl), 2) : 0m;
        result.AvgLoss = losses.Count > 0 ? Math.Round(losses.Average(t => t.Pnl), 2) : 0m;
        result.ProfitFactor = losses.Sum(t => Math.Abs(t.Pnl)) > 0m
            ? Math.Round(wins.Sum(t => t.Pnl) / losses.Sum(t => Math.Abs(t.Pnl)), 2) : 0m;
        if (dailyReturns.Count > 1)
        {
            var avg = dailyReturns.Average();
            var std = (decimal)Math.Sqrt((double)dailyReturns.Select(r => (r - avg) * (r - avg)).Average());
            result.SharpeRatio = std > 0m ? Math.Round(avg / std * (decimal)Math.Sqrt(252), 2) : 0m;
        }
        return result;
    }

    /// <summary>多空版 walk-forward(切窗邏輯同 BacktestEngine.RunWalkForward、改用本引擎 Run)。</summary>
    public static BacktestEngine.WalkForwardResult RunWalkForward(
        IStrategy strategy, List<BarData> bars, StrategyConfig config,
        int trainBars = 250, int testBars = 90, int stride = 60,
        decimal initialCash = 100_000, decimal commission = 0.001m, decimal slippagePct = 0m,
        bool confidenceSizing = false,
        decimal atrTrailMultiplier = 0m,   // H18:>0 啟用 ATR trailing SL
        int atrPeriod = 14,
        decimal defaultInitialSlPct = 0m,  // H18 補正:trail 啟用 + 策略未 emit 時的初始 SL %
        decimal peakTrailTriggerPct = 0m,  // H18 live-align:peak-based trail trigger %
        decimal peakTrailDistancePct = 0m, // peak-based trail distance %
        decimal beTriggerPct = 0m,         // 2026-06-04 live-align:BE-move trigger %(轉發給 Run)
        decimal beBufferPct = 0m,          // BE buffer %
        bool applyFunding = false,         // D 路線:funding 計入 PnL(bars 需先注入 FundingRate)
        bool volTargetSizing = false,      // Q1.2 vol-targeting(透傳 Run)
        decimal volTargetAnnual = 0.60m,
        int volTargetLookback = 30,
        decimal volTargetMaxScalar = 2.0m,
        int embargoBars = 0)   // #1 LdP embargo:train↔test 間插 gap、排除邊界序列相關洩漏(0=關、預設行為不變)
    {
        var result = new BacktestEngine.WalkForwardResult
        {
            Strategy = strategy.Name, Symbol = config.Symbol,
            TrainBars = trainBars, TestBars = testBars, Stride = stride,
        };
        var requiredPerFold = trainBars + embargoBars + testBars;
        if (bars.Count < requiredPerFold || trainBars < 50 || testBars < 10 || stride < 1) return result;

        int foldIdx = 0;
        for (int start = 0; start + requiredPerFold <= bars.Count; start += stride)
        {
            var trainSlice = bars.GetRange(start, trainBars);
            var trainBt = Run(strategy, trainSlice, config, initialCash, commission, slippagePct: slippagePct, confidenceSizing: confidenceSizing, atrTrailMultiplier: atrTrailMultiplier, atrPeriod: atrPeriod, defaultInitialSlPct: defaultInitialSlPct, peakTrailTriggerPct: peakTrailTriggerPct, peakTrailDistancePct: peakTrailDistancePct, beTriggerPct: beTriggerPct, beBufferPct: beBufferPct, applyFunding: applyFunding, volTargetSizing: volTargetSizing, volTargetAnnual: volTargetAnnual, volTargetLookback: volTargetLookback, volTargetMaxScalar: volTargetMaxScalar);
            var testWindow = bars.GetRange(start, trainBars + embargoBars + testBars);   // embargo gap 也餵進來當 warm-up、但不交易
            var testBt = Run(strategy, testWindow, config, initialCash, commission,
                tradeStartIndex: trainBars + embargoBars, slippagePct: slippagePct, confidenceSizing: confidenceSizing,
                atrTrailMultiplier: atrTrailMultiplier, atrPeriod: atrPeriod, defaultInitialSlPct: defaultInitialSlPct,
                peakTrailTriggerPct: peakTrailTriggerPct, peakTrailDistancePct: peakTrailDistancePct, beTriggerPct: beTriggerPct, beBufferPct: beBufferPct, applyFunding: applyFunding,
                volTargetSizing: volTargetSizing, volTargetAnnual: volTargetAnnual, volTargetLookback: volTargetLookback, volTargetMaxScalar: volTargetMaxScalar);
            result.Folds.Add(new BacktestEngine.WalkForwardFold
            {
                FoldIndex = foldIdx++,
                TrainStart = trainSlice.First().OpenTime, TrainEnd = trainSlice.Last().OpenTime,
                TestStart = bars[start + trainBars + embargoBars].OpenTime, TestEnd = bars[start + trainBars + embargoBars + testBars - 1].OpenTime,
                Train = trainBt, Test = testBt,
            });
        }
        if (result.Folds.Count == 0) return result;

        var testReturns = result.Folds.Select(f => f.Test!.TotalReturnPct).ToList();
        var trainReturns = result.Folds.Select(f => f.Train!.TotalReturnPct).ToList();
        var testSharpes = result.Folds.Select(f => f.Test!.SharpeRatio).ToList();
        result.TotalFolds = result.Folds.Count;
        result.PositiveTestFolds = testReturns.Count(r => r > 0);
        result.AvgTestReturnPct = Math.Round(testReturns.Average(), 2);
        result.MedianTestReturnPct = Math.Round(Median(testReturns), 2);
        result.AvgTestSharpe = Math.Round(testSharpes.Average(), 2);
        result.AvgTestWinRate = Math.Round(result.Folds.Select(f => f.Test!.WinRate).Average(), 2);
        result.WorstTestDdPct = Math.Round(result.Folds.Select(f => f.Test!.MaxDrawdownPct).Max(), 2);
        result.IsOosReturnGap = Math.Round(trainReturns.Average() - testReturns.Average(), 2);
        return result;
    }

    /// <summary>
    /// H18 helper:ATR at bar idx,用過去 atrPeriod 根 TR 平均。
    /// idx<period 或資料不夠 → 回 0(caller 會 skip trail 更新)。
    /// </summary>
    private static decimal ComputeAtrAt(List<BarData> bars, int idx, int period)
    {
        if (idx < period || period < 1) return 0m;
        decimal sum = 0m;
        for (int i = idx - period + 1; i <= idx; i++)
        {
            var hi = bars[i].High;
            var lo = bars[i].Low;
            var prevClose = bars[i - 1].Close;
            var tr = Math.Max(hi - lo, Math.Max(Math.Abs(hi - prevClose), Math.Abs(lo - prevClose)));
            sum += tr;
        }
        return sum / period;
    }

    /// <summary>
    /// 多空組合層:N 支策略各用 1/N 資金、獨立多空持倉,逐根把權益曲線相加。
    /// 各 sub 用同一 loop 起點(lookback)→ EquityCurve 對齊 index、可逐點相加。
    /// </summary>
    public static BacktestEngine.BacktestResult RunPortfolio(
        List<IStrategy> strategies, List<BarData> bars, StrategyConfig config,
        decimal initialCash = 100_000, decimal commission = 0.001m,
        int tradeStartIndex = 0, decimal slippagePct = 0m, decimal notionalPct = 0.95m)
    {
        var result = new BacktestEngine.BacktestResult
        {
            Strategy = "ls_portfolio", Symbol = config.Symbol, InitialCash = initialCash, TotalBars = bars.Count,
            StartDate = bars.FirstOrDefault()?.OpenTime ?? DateTime.MinValue,
            EndDate = bars.LastOrDefault()?.OpenTime ?? DateTime.MinValue,
        };
        if (strategies == null || strategies.Count == 0 || bars.Count < 50) return result;

        var per = initialCash / strategies.Count;
        var subs = strategies
            .Select(s => Run(s, bars, config, per, commission, tradeStartIndex, slippagePct, notionalPct))
            .ToList();
        int len = subs.Min(b => b.EquityCurve.Count);
        if (len == 0) return result;

        decimal peak = initialCash, maxDd = 0m, prev = initialCash;
        var dailyRet = new List<decimal>();
        for (int t = 0; t < len; t++)
        {
            decimal eq = 0m;
            foreach (var b in subs) eq += b.EquityCurve[t].Value;
            result.EquityCurve.Add(new BacktestEngine.EquityPoint { Date = subs[0].EquityCurve[t].Date, Value = eq });
            if (prev > 0m) dailyRet.Add((eq - prev) / prev);
            prev = eq;
            if (eq > peak) peak = eq;
            var dd = peak - eq; if (dd > maxDd) maxDd = dd;
        }
        foreach (var b in subs) result.Trades.AddRange(b.Trades);

        var finalValue = subs.Sum(b => b.FinalValue);
        result.FinalValue = finalValue;
        result.TotalReturn = Math.Round(finalValue - initialCash, 2);
        result.TotalReturnPct = Math.Round((finalValue - initialCash) / initialCash * 100m, 2);
        result.TotalTrades = result.Trades.Count;
        result.WinTrades = result.Trades.Count(t => t.Pnl > 0);
        result.LoseTrades = result.Trades.Count(t => t.Pnl <= 0);
        result.WinRate = result.TotalTrades > 0 ? Math.Round((decimal)result.WinTrades / result.TotalTrades * 100m, 1) : 0m;
        result.MaxDrawdown = Math.Round(maxDd, 2);
        result.MaxDrawdownPct = peak > 0m ? Math.Round(maxDd / peak * 100m, 2) : 0m;
        if (dailyRet.Count > 1)
        {
            var avg = dailyRet.Average();
            var std = (decimal)Math.Sqrt((double)dailyRet.Select(r => (r - avg) * (r - avg)).Average());
            result.SharpeRatio = std > 0m ? Math.Round(avg / std * (decimal)Math.Sqrt(252), 2) : 0m;
        }
        return result;
    }

    /// <summary>多空組合的 walk-forward OOS(切窗同 RunWalkForward、每窗用 RunPortfolio)。</summary>
    public static BacktestEngine.WalkForwardResult RunPortfolioWalkForward(
        List<IStrategy> strategies, List<BarData> bars, StrategyConfig config,
        int trainBars = 250, int testBars = 90, int stride = 60,
        decimal initialCash = 100_000, decimal commission = 0.001m, decimal slippagePct = 0m)
    {
        var result = new BacktestEngine.WalkForwardResult
        {
            Strategy = "ls_portfolio", Symbol = config.Symbol,
            TrainBars = trainBars, TestBars = testBars, Stride = stride,
        };
        var requiredPerFold = trainBars + testBars;
        if (bars.Count < requiredPerFold || trainBars < 50 || testBars < 10 || stride < 1) return result;

        int foldIdx = 0;
        for (int start = 0; start + requiredPerFold <= bars.Count; start += stride)
        {
            var trainSlice = bars.GetRange(start, trainBars);
            var trainBt = RunPortfolio(strategies, trainSlice, config, initialCash, commission, slippagePct: slippagePct);
            var testWindow = bars.GetRange(start, trainBars + testBars);
            var testBt = RunPortfolio(strategies, testWindow, config, initialCash, commission,
                tradeStartIndex: trainBars, slippagePct: slippagePct);
            result.Folds.Add(new BacktestEngine.WalkForwardFold
            {
                FoldIndex = foldIdx++,
                TrainStart = trainSlice.First().OpenTime, TrainEnd = trainSlice.Last().OpenTime,
                TestStart = bars[start + trainBars].OpenTime, TestEnd = bars[start + trainBars + testBars - 1].OpenTime,
                Train = trainBt, Test = testBt,
            });
        }
        if (result.Folds.Count == 0) return result;

        var testReturns = result.Folds.Select(f => f.Test!.TotalReturnPct).ToList();
        var trainReturns = result.Folds.Select(f => f.Train!.TotalReturnPct).ToList();
        var testSharpes = result.Folds.Select(f => f.Test!.SharpeRatio).ToList();
        result.TotalFolds = result.Folds.Count;
        result.PositiveTestFolds = testReturns.Count(r => r > 0);
        result.AvgTestReturnPct = Math.Round(testReturns.Average(), 2);
        result.MedianTestReturnPct = Math.Round(Median(testReturns), 2);
        result.AvgTestSharpe = Math.Round(testSharpes.Average(), 2);
        result.WorstTestDdPct = Math.Round(result.Folds.Select(f => f.Test!.MaxDrawdownPct).Max(), 2);
        result.IsOosReturnGap = Math.Round(trainReturns.Average() - testReturns.Average(), 2);
        return result;
    }

    private static decimal Median(List<decimal> xs)
    {
        if (xs.Count == 0) return 0m;
        var s = xs.OrderBy(x => x).ToList(); int n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2m;
    }
}
