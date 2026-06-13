using StrategyWorker.Models;

namespace StrategyWorker.Engine;

/// <summary>
/// 回測引擎 — 用歷史 K 線模擬策略執行。
/// </summary>
public class BacktestEngine
{
    /// <summary>
    /// 回測結果。
    /// </summary>
    public class BacktestResult
    {
        public string Strategy     { get; set; } = "";
        public string Symbol       { get; set; } = "";
        public decimal InitialCash { get; set; }
        public decimal FinalValue  { get; set; }
        public decimal TotalReturn { get; set; }
        public decimal TotalReturnPct { get; set; }
        public int TotalTrades    { get; set; }
        public int WinTrades      { get; set; }
        public int LoseTrades     { get; set; }
        public decimal WinRate    { get; set; }
        public decimal MaxDrawdown { get; set; }
        public decimal MaxDrawdownPct { get; set; }
        public decimal SharpeRatio { get; set; }
        public decimal AvgWin     { get; set; }
        public decimal AvgLoss    { get; set; }
        public decimal ProfitFactor { get; set; }
        public DateTime StartDate { get; set; }
        public DateTime EndDate   { get; set; }
        public int TotalBars      { get; set; }
        public List<BacktestTrade> Trades { get; set; } = new();
        public List<EquityPoint> EquityCurve { get; set; } = new();
    }

    public class BacktestTrade
    {
        public string Side     { get; set; } = "";
        public DateTime EntryDate { get; set; }
        public decimal EntryPrice { get; set; }
        public DateTime ExitDate  { get; set; }
        public decimal ExitPrice  { get; set; }
        public decimal Quantity   { get; set; }
        public decimal Pnl       { get; set; }
        public decimal PnlPct    { get; set; }
        public int HoldBars      { get; set; }
        public decimal EntryConfidence { get; set; }  // 進場當下 signal.Confidence(confidence 校準診斷用;0=未記錄)
    }

    public class EquityPoint
    {
        public DateTime Date  { get; set; }
        public decimal Value  { get; set; }
    }

    /// <summary>
    /// 執行回測。
    ///
    /// htfBars（選用、Batch HTF backtest 新增）：給支援多時間框架的策略
    /// （目前是 HarmonicStrategy）用。每個 step 引擎會以 LTF[i].OpenTime 為界、
    /// 將 htfBars 切到對應位置塞回 config.HtfBars。沒提供就跟既有單時間框架行為一致。
    /// </summary>
    public static BacktestResult Run(
        IStrategy strategy,
        List<BarData> bars,
        StrategyConfig config,
        decimal initialCash = 100_000,
        decimal commission = 0.001m, // 0.1% 手續費
        List<BarData>? htfBars = null,
        int tradeStartIndex = 0,     // 從第幾根才開始交易/計績效；前面的 bar 只當指標 warmup（walk-forward OOS 用）
        decimal slippagePct = 0m,    // 每邊滑價（併入成本率；0=不計、向後相容）
        bool applyFunding = false,   // 永續資金費:持倉每根依 funding_rate 計成本/收益(需 bars 帶 FundingRate)
        decimal forcedStopPct = 0m)  // 強制固定停損 %(0=不強制、用 signal 自帶 stop);給「有停損 vs 無停損」對照用
    {
        // 滑價當「每邊額外成本」併入手續費(標準近似、比逐筆改成交價穩當)。
        var costRate = commission + slippagePct;
        // 資金費每根佔 8h funding 週期的比例(1d=3 次/天=3、1h=1/8…);算不出 interval 就當 1。
        var fundingPeriodsPerBar = applyFunding ? IntervalToFundingPeriods(config.Interval) : 0m;
        var result = new BacktestResult
        {
            Strategy    = strategy.Name,
            Symbol      = config.Symbol,
            InitialCash = initialCash,
            TotalBars   = bars.Count,
            StartDate   = bars.FirstOrDefault()?.OpenTime ?? DateTime.MinValue,
            EndDate     = bars.LastOrDefault()?.OpenTime ?? DateTime.MinValue,
        };

        if (bars.Count < 50) return result;

        decimal cash = initialCash;
        decimal position = 0;        // 持有數量
        decimal entryPrice = 0;
        decimal targetPrice = 0;     // 開倉時鎖定的停利目標（0 = 未啟用、靠反向訊號平倉）
        decimal stopPrice = 0;       // 開倉時鎖定的停損
        DateTime entryDate = DateTime.MinValue;
        int entryIndex = -1;         // 進場 bar index（算資金費累計用）
        decimal peakEquity = initialCash;
        decimal maxDrawdown = 0;
        var dailyReturns = new List<decimal>();
        decimal prevEquity = initialCash;

        // 從第 50 根 bar 開始（確保有足夠歷史做指標計算）；walk-forward OOS 時 tradeStartIndex
        // 把進場往後推到 test 區間起點，但 Evaluate 仍吃得到前面整段 train 當 warmup（指標不被截斷）。
        int lookback = Math.Max(Math.Max(config.SmaSlow + 5, 50), tradeStartIndex);

        // HTF 指標：保留原 config.HtfBars，每步覆寫成截至當前 LTF 時間的 HTF 切片、結束後還原
        var originalHtf = config.HtfBars;
        bool useHtf = htfBars != null && htfBars.Count >= 2;
        int htfPtr = 0;   // htfBars 中最後一個 OpenTime ≤ 當前 LTF 時間的 index

        try
        {
        for (int i = lookback; i < bars.Count; i++)
        {
            var windowBars = bars.GetRange(0, i + 1);

            // 把 HTF 切到對應時間（HTF 通常更稀疏、scan 比 binary search 簡單且 monotonic）
            if (useHtf)
            {
                var ltfTime = bars[i].OpenTime;
                while (htfPtr < htfBars!.Count - 1 && htfBars[htfPtr + 1].OpenTime <= ltfTime)
                    htfPtr++;
                // 至少要 2 根才有意義；少於就傳 null（策略會略過 HTF 邏輯）
                config.HtfBars = htfPtr >= 1
                    ? htfBars.GetRange(0, htfPtr + 1)
                    : null;
            }

            var signal = strategy.Evaluate(windowBars, config);
            var currentPrice = bars[i].Close;
            var equity = cash + position * currentPrice;

            // 紀錄 equity curve
            result.EquityCurve.Add(new EquityPoint { Date = bars[i].OpenTime, Value = equity });

            // 紀錄日報酬
            if (prevEquity > 0)
                dailyReturns.Add((equity - prevEquity) / prevEquity);
            prevEquity = equity;

            // 追蹤 max drawdown
            if (equity > peakEquity) peakEquity = equity;
            var drawdown = peakEquity - equity;
            if (drawdown > maxDrawdown) maxDrawdown = drawdown;

            // 停利/停損（opt-in：只在開倉時鎖定了 target/stop 才檢查；用「本根」high/low 觸發、
            // 倉位是前面的 bar 開的 → 無 lookahead）。同根同時觸 SL 與 TP 時保守假設先觸 SL。
            bool exitedThisBar = false;
            if (position > 0 && (targetPrice > 0 || stopPrice > 0))
            {
                decimal hi = bars[i].High, lo = bars[i].Low;
                decimal exitPx = 0; string exitTag = "";
                if (stopPrice > 0 && lo <= stopPrice) { exitPx = stopPrice; exitTag = "long (SL)"; }
                else if (targetPrice > 0 && hi >= targetPrice) { exitPx = targetPrice; exitTag = "long (TP)"; }

                if (exitPx > 0)
                {
                    var sellValue = position * exitPx;
                    var fee = sellValue * costRate;
                    var funding = ComputeFundingCost(bars, entryIndex, i, position, fundingPeriodsPerBar);
                    cash += sellValue - fee - funding;
                    var pnl = (exitPx - entryPrice) * position - (entryPrice * position * costRate) - fee - funding;
                    var pnlPct = entryPrice > 0 ? (exitPx - entryPrice) / entryPrice * 100 : 0;
                    result.Trades.Add(new BacktestTrade
                    {
                        Side = exitTag, EntryDate = entryDate, EntryPrice = entryPrice,
                        ExitDate = bars[i].OpenTime, ExitPrice = exitPx,
                        Quantity = position, Pnl = Math.Round(pnl, 2), PnlPct = Math.Round(pnlPct, 2),
                        HoldBars = i - bars.FindIndex(b => b.OpenTime == entryDate),
                    });
                    position = 0; entryPrice = 0; targetPrice = 0; stopPrice = 0; entryIndex = -1;
                    exitedThisBar = true;
                }
            }

            // 交易邏輯（本根已被 TP/SL 平掉就不再進出場、避免同根立刻回補）
            if (exitedThisBar)
            {
                // no-op：本根已出場
            }
            else if (signal.Action == "buy" && signal.Confidence >= 0.6m && position == 0)
            {
                // 開多倉 — 用 90% 資金
                var orderValue = cash * 0.9m;
                var fee = orderValue * costRate;
                var qty = (orderValue - fee) / currentPrice;
                position = qty;
                entryPrice = currentPrice;
                entryDate = bars[i].OpenTime;
                entryIndex = i;
                cash -= orderValue;
                targetPrice = signal.TargetPrice ?? 0;   // 鎖定本次進場的停利/停損
                stopPrice = signal.StopPrice ?? 0;
                // 強制固定停損(對照用):signal 沒帶 stop 時、用 entry × (1 − forcedStopPct%) 套一個。
                // 讓本來「抱到反彈」的均值回歸策略(rsi_stoch/mfi)能跑「有停損」版做比較。
                if (forcedStopPct > 0m && stopPrice <= 0m)
                    stopPrice = entryPrice * (1m - forcedStopPct / 100m);
            }
            else if (signal.Action == "sell" && position > 0)
            {
                // 平倉
                var sellValue = position * currentPrice;
                var fee = sellValue * costRate;
                var funding = ComputeFundingCost(bars, entryIndex, i, position, fundingPeriodsPerBar);
                cash += sellValue - fee - funding;

                var pnl = (currentPrice - entryPrice) * position - (entryPrice * position * costRate) - fee - funding;
                var pnlPct = entryPrice > 0 ? (currentPrice - entryPrice) / entryPrice * 100 : 0;

                result.Trades.Add(new BacktestTrade
                {
                    Side = "long", EntryDate = entryDate, EntryPrice = entryPrice,
                    ExitDate = bars[i].OpenTime, ExitPrice = currentPrice,
                    Quantity = position, Pnl = Math.Round(pnl, 2),
                    PnlPct = Math.Round(pnlPct, 2),
                    HoldBars = i - bars.FindIndex(b => b.OpenTime == entryDate),
                });

                position = 0;
                entryPrice = 0;
                targetPrice = 0;
                stopPrice = 0;
                entryIndex = -1;
            }
        }
        }
        finally
        {
            // 還原 caller 傳進來的原始 HtfBars、避免污染外部 config 物件
            config.HtfBars = originalHtf;
        }

        // 如果還有持倉，用最後收盤價平倉
        if (position > 0)
        {
            var lastPrice = bars[^1].Close;
            var sellValue = position * lastPrice;
            var fee = sellValue * costRate;
            var funding = ComputeFundingCost(bars, entryIndex, bars.Count - 1, position, fundingPeriodsPerBar);
            cash += sellValue - fee - funding;

            var pnl = (lastPrice - entryPrice) * position - (entryPrice * position * costRate) - fee - funding;
            result.Trades.Add(new BacktestTrade
            {
                Side = "long (auto-close)", EntryDate = entryDate, EntryPrice = entryPrice,
                ExitDate = bars[^1].OpenTime, ExitPrice = lastPrice,
                Quantity = position, Pnl = Math.Round(pnl, 2),
                PnlPct = entryPrice > 0 ? Math.Round((lastPrice - entryPrice) / entryPrice * 100, 2) : 0,
            });
            position = 0;
        }

        // 統計
        result.FinalValue     = cash;
        result.TotalReturn    = Math.Round(cash - initialCash, 2);
        result.TotalReturnPct = Math.Round((cash - initialCash) / initialCash * 100, 2);
        result.TotalTrades    = result.Trades.Count;
        result.WinTrades      = result.Trades.Count(t => t.Pnl > 0);
        result.LoseTrades     = result.Trades.Count(t => t.Pnl <= 0);
        result.WinRate        = result.TotalTrades > 0 ? Math.Round((decimal)result.WinTrades / result.TotalTrades * 100, 1) : 0;
        result.MaxDrawdown    = Math.Round(maxDrawdown, 2);
        result.MaxDrawdownPct = peakEquity > 0 ? Math.Round(maxDrawdown / peakEquity * 100, 2) : 0;

        var wins = result.Trades.Where(t => t.Pnl > 0).ToList();
        var losses = result.Trades.Where(t => t.Pnl <= 0).ToList();
        result.AvgWin  = wins.Count > 0 ? Math.Round(wins.Average(t => t.Pnl), 2) : 0;
        result.AvgLoss = losses.Count > 0 ? Math.Round(losses.Average(t => t.Pnl), 2) : 0;
        result.ProfitFactor = losses.Sum(t => Math.Abs(t.Pnl)) > 0
            ? Math.Round(wins.Sum(t => t.Pnl) / losses.Sum(t => Math.Abs(t.Pnl)), 2) : 0;

        // Sharpe Ratio (annualized, assuming daily)
        if (dailyReturns.Count > 1)
        {
            var avg = dailyReturns.Average();
            var std = (decimal)Math.Sqrt((double)dailyReturns.Select(r => (r - avg) * (r - avg)).Average());
            result.SharpeRatio = std > 0 ? Math.Round(avg / std * (decimal)Math.Sqrt(252), 2) : 0;
        }

        return result;
    }

    /// <summary>
    /// 持倉期間累計資金費(永續)。多單:funding_rate>0=付出(成本)、&lt;0=收取(回正自然處理)。
    /// 從 entryIdx+1 累到 exitIdx(進場那根不收);每根 = rate × notional(position×close) × periodsPerBar。
    /// 沒 FundingRate 的 bar 跳過(非 perp / 無資料 → 不計)。
    /// </summary>
    private static decimal ComputeFundingCost(
        List<BarData> bars, int entryIdx, int exitIdx, decimal position, decimal periodsPerBar)
    {
        if (entryIdx < 0 || periodsPerBar <= 0m || position <= 0m) return 0m;
        decimal total = 0m;
        for (int k = entryIdx + 1; k <= exitIdx && k < bars.Count; k++)
            if (bars[k].FundingRate is decimal fr)
                total += fr * position * bars[k].Close * periodsPerBar;
        return total;
    }

    /// <summary>每根 bar 涵蓋幾個 8h 資金費週期(1d=3、4h=0.5、1h=1/8…)。未知 interval → 當 1。</summary>
    private static decimal IntervalToFundingPeriods(string interval) => (interval switch
    {
        "15m" => 0.25m, "30m" => 0.5m, "1h" => 1m, "2h" => 2m, "4h" => 4m,
        "6h" => 6m, "12h" => 12m, "1d" => 24m, "3d" => 72m, "1w" => 168m,
        _ => 8m,   // 未知 → 當 8h(=1 個 funding 週期)
    }) / 8m;

    /// <summary>
    /// Walk-forward 回測 fold——記錄一個 train/test 切窗的結果。
    /// </summary>
    public class WalkForwardFold
    {
        public int FoldIndex { get; set; }
        public DateTime TrainStart { get; set; }
        public DateTime TrainEnd   { get; set; }
        public DateTime TestStart  { get; set; }
        public DateTime TestEnd    { get; set; }
        public BacktestResult? Train { get; set; }  // 訓練窗（in-sample）回測結果
        public BacktestResult? Test  { get; set; }  // 測試窗（out-of-sample）回測結果
    }

    /// <summary>
    /// Walk-forward 整體結果——所有 fold + 聚合 OOS 指標。
    /// </summary>
    public class WalkForwardResult
    {
        public string Strategy   { get; set; } = "";
        public string Symbol     { get; set; } = "";
        public int TrainBars     { get; set; }
        public int TestBars      { get; set; }
        public int Stride        { get; set; }
        public List<WalkForwardFold> Folds { get; set; } = new();

        // 聚合 OOS 指標——每 fold 的 test 結果平均/總和
        public decimal AvgTestReturnPct  { get; set; }
        public decimal MedianTestReturnPct { get; set; }
        public decimal AvgTestSharpe     { get; set; }
        public decimal AvgTestWinRate    { get; set; }
        public decimal WorstTestDdPct    { get; set; }
        public int     PositiveTestFolds { get; set; }
        public int     TotalFolds        { get; set; }

        // In-sample / out-of-sample gap——過擬合的指標。差距大 = 訓練看起來好但測試垃圾
        public decimal IsOosReturnGap    { get; set; }  // (avg train return) - (avg test return)
        public decimal IsOosSharpeGap    { get; set; }
    }

    /// <summary>
    /// Walk-forward 回測——把 bars 切成 [train, test, train, test, ...] 滑動視窗，
    /// 每個 fold 用 train 窗回測算 in-sample 指標，再用 test 窗做 out-of-sample 驗證。
    ///
    /// 標準學術 backtest 嚴謹度高、可看出策略是否過擬合：
    ///   - 如果 train 報酬遠優於 test 報酬 → 過擬合（看歷史撿便宜）
    ///   - 如果 train 跟 test 差不多 → 真有 alpha
    ///
    /// 預設 train=180、test=60、stride=30——對日線約是 9 個月訓練 / 3 個月測試 / 1.5 月間隔。
    /// </summary>
    public static WalkForwardResult RunWalkForward(
        IStrategy strategy,
        List<BarData> bars,
        StrategyConfig config,
        int trainBars = 180,
        int testBars  = 60,
        int stride    = 30,
        decimal initialCash = 100_000,
        decimal commission = 0.001m,
        decimal slippagePct = 0m,
        bool applyFunding = false,
        decimal forcedStopPct = 0m,  // 強制固定停損 %(對照「有停損 vs 無停損」用;0=無停損、抱到反向訊號)
        int embargoBars = 0)         // #1 LdP embargo:train↔test 間插 gap、排除邊界序列相關洩漏(0=關、預設行為不變)
    {
        var result = new WalkForwardResult
        {
            Strategy = strategy.Name,
            Symbol   = config.Symbol,
            TrainBars = trainBars,
            TestBars  = testBars,
            Stride    = stride,
        };

        var requiredPerFold = trainBars + embargoBars + testBars;
        if (bars.Count < requiredPerFold) return result;
        if (trainBars < 50 || testBars < 10 || stride < 1) return result;

        // 從 0 開始，每 stride 切一個 fold；最後一個 fold 必須完整覆蓋 train+test
        int foldIdx = 0;
        for (int start = 0; start + requiredPerFold <= bars.Count; start += stride)
        {
            var trainSlice = bars.GetRange(start, trainBars);
            var testSlice  = bars.GetRange(start + trainBars + embargoBars, testBars);   // embargo:test 起點往後推 gap、排除邊界序列相關

            // 訓練窗：直接跑 backtest
            var trainBt = Run(strategy, trainSlice, config, initialCash, commission,
                slippagePct: slippagePct, applyFunding: applyFunding, forcedStopPct: forcedStopPct);
            // 測試窗（OOS）：餵 [train+embargo+test] 整段、但只從 test 起點開始交易（tradeStartIndex=trainBars+embargoBars）。
            // 這樣指標在 test 區間有完整 train(+embargo gap)當 warmup（不被截斷）、又不偷看 test 內未來——
            // 修掉「bare 90 根 test slice 害 MinBars>90 的策略永遠 hold / 指標被截斷」的方法學 bug。
            var testWindow = bars.GetRange(start, trainBars + embargoBars + testBars);
            var testBt  = Run(strategy, testWindow, config, initialCash, commission,
                tradeStartIndex: trainBars + embargoBars, slippagePct: slippagePct, applyFunding: applyFunding,
                forcedStopPct: forcedStopPct);

            result.Folds.Add(new WalkForwardFold
            {
                FoldIndex  = foldIdx++,
                TrainStart = trainSlice.First().OpenTime,
                TrainEnd   = trainSlice.Last().OpenTime,
                TestStart  = testSlice.First().OpenTime,
                TestEnd    = testSlice.Last().OpenTime,
                Train      = trainBt,
                Test       = testBt,
            });
        }

        // 聚合 OOS 統計
        if (result.Folds.Count == 0) return result;
        var testReturns = result.Folds.Where(f => f.Test != null).Select(f => f.Test!.TotalReturnPct).ToList();
        var trainReturns = result.Folds.Where(f => f.Train != null).Select(f => f.Train!.TotalReturnPct).ToList();
        var testSharpes  = result.Folds.Where(f => f.Test != null).Select(f => f.Test!.SharpeRatio).ToList();
        var trainSharpes = result.Folds.Where(f => f.Train != null).Select(f => f.Train!.SharpeRatio).ToList();
        var testWins     = result.Folds.Where(f => f.Test != null).Select(f => f.Test!.WinRate).ToList();
        var testDds      = result.Folds.Where(f => f.Test != null).Select(f => f.Test!.MaxDrawdownPct).ToList();

        result.TotalFolds         = result.Folds.Count;
        result.PositiveTestFolds  = testReturns.Count(r => r > 0);
        result.AvgTestReturnPct   = testReturns.Count > 0 ? Math.Round(testReturns.Average(), 2) : 0;
        result.MedianTestReturnPct = testReturns.Count > 0 ? Math.Round(Median(testReturns), 2) : 0;
        result.AvgTestSharpe      = testSharpes.Count > 0 ? Math.Round(testSharpes.Average(), 2) : 0;
        result.AvgTestWinRate     = testWins.Count > 0 ? Math.Round(testWins.Average(), 2) : 0;
        result.WorstTestDdPct     = testDds.Count > 0 ? Math.Round(testDds.Max(), 2) : 0;  // DdPct 是正數、越大越差
        result.IsOosReturnGap     = trainReturns.Count > 0 && testReturns.Count > 0
            ? Math.Round(trainReturns.Average() - testReturns.Average(), 2) : 0;
        result.IsOosSharpeGap     = trainSharpes.Count > 0 && testSharpes.Count > 0
            ? Math.Round(trainSharpes.Average() - testSharpes.Average(), 2) : 0;

        return result;
    }

    private static decimal Median(List<decimal> xs)
    {
        if (xs.Count == 0) return 0;
        var sorted = xs.OrderBy(x => x).ToList();
        int n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2m;
    }

    /// <summary>
    /// 組合層回測(portfolio layer)—— 把 N 條策略「各用 1/N 資金、獨立持倉」、逐根把權益曲線相加。
    /// 這才能捕捉「不相關 edge 合在一起波動下降」的紅利(投票會在訊號不一致時退成 hold、反而抹掉紅利)。
    /// 各 sub 用同一 loop 起點(lookback) → EquityCurve 對齊 index、可逐點相加。Sharpe/DD 從合併權益算。
    /// </summary>
    public static BacktestResult RunPortfolio(
        List<IStrategy> strategies, List<BarData> bars, StrategyConfig config,
        decimal initialCash = 100_000, decimal commission = 0.001m,
        int tradeStartIndex = 0, decimal slippagePct = 0m, bool applyFunding = false)
    {
        var result = new BacktestResult
        {
            Strategy = "portfolio", Symbol = config.Symbol, InitialCash = initialCash, TotalBars = bars.Count,
            StartDate = bars.FirstOrDefault()?.OpenTime ?? DateTime.MinValue,
            EndDate = bars.LastOrDefault()?.OpenTime ?? DateTime.MinValue,
        };
        if (strategies == null || strategies.Count == 0 || bars.Count < 50) return result;

        var per = initialCash / strategies.Count;
        var subs = strategies
            .Select(s => Run(s, bars, config, per, commission, null, tradeStartIndex, slippagePct, applyFunding))
            .ToList();

        int len = subs.Min(b => b.EquityCurve.Count);
        if (len == 0) return result;

        decimal peak = initialCash, maxDd = 0m, prev = initialCash;
        var dailyRet = new List<decimal>();
        for (int t = 0; t < len; t++)
        {
            decimal eq = 0m;
            foreach (var b in subs) eq += b.EquityCurve[t].Value;
            result.EquityCurve.Add(new EquityPoint { Date = subs[0].EquityCurve[t].Date, Value = eq });
            if (prev > 0) dailyRet.Add((eq - prev) / prev);
            prev = eq;
            if (eq > peak) peak = eq;
            var dd = peak - eq; if (dd > maxDd) maxDd = dd;
        }
        foreach (var b in subs) result.Trades.AddRange(b.Trades);

        var finalValue = subs.Sum(b => b.FinalValue);
        result.FinalValue     = finalValue;
        result.TotalReturn    = Math.Round(finalValue - initialCash, 2);
        result.TotalReturnPct = Math.Round((finalValue - initialCash) / initialCash * 100, 2);
        result.TotalTrades    = result.Trades.Count;
        result.WinTrades      = result.Trades.Count(t => t.Pnl > 0);
        result.LoseTrades     = result.Trades.Count(t => t.Pnl <= 0);
        result.WinRate        = result.TotalTrades > 0 ? Math.Round((decimal)result.WinTrades / result.TotalTrades * 100, 1) : 0;
        result.MaxDrawdown    = Math.Round(maxDd, 2);
        result.MaxDrawdownPct = peak > 0 ? Math.Round(maxDd / peak * 100, 2) : 0;
        if (dailyRet.Count > 1)
        {
            var avg = dailyRet.Average();
            var std = (decimal)Math.Sqrt((double)dailyRet.Select(r => (r - avg) * (r - avg)).Average());
            result.SharpeRatio = std > 0 ? Math.Round(avg / std * (decimal)Math.Sqrt(252), 2) : 0;
        }
        return result;
    }

    /// <summary>組合層 walk-forward:同 RunWalkForward 的切窗,但每窗用 RunPortfolio(N 條各半)。</summary>
    public static WalkForwardResult RunPortfolioWalkForward(
        List<IStrategy> strategies, List<BarData> bars, StrategyConfig config,
        int trainBars = 365, int testBars = 90, int stride = 90,
        decimal initialCash = 100_000, decimal commission = 0.001m,
        decimal slippagePct = 0m, bool applyFunding = false)
    {
        var result = new WalkForwardResult
        {
            Strategy = "portfolio", Symbol = config.Symbol,
            TrainBars = trainBars, TestBars = testBars, Stride = stride,
        };
        var requiredPerFold = trainBars + testBars;
        if (bars.Count < requiredPerFold || trainBars < 50 || testBars < 10 || stride < 1) return result;

        int foldIdx = 0;
        for (int start = 0; start + requiredPerFold <= bars.Count; start += stride)
        {
            var trainSlice = bars.GetRange(start, trainBars);
            var testSlice  = bars.GetRange(start + trainBars, testBars);
            var trainBt = RunPortfolio(strategies, trainSlice, config, initialCash, commission,
                slippagePct: slippagePct, applyFunding: applyFunding);
            var testWindow = bars.GetRange(start, trainBars + testBars);
            var testBt  = RunPortfolio(strategies, testWindow, config, initialCash, commission,
                tradeStartIndex: trainBars, slippagePct: slippagePct, applyFunding: applyFunding);
            result.Folds.Add(new WalkForwardFold
            {
                FoldIndex = foldIdx++,
                TrainStart = trainSlice.First().OpenTime, TrainEnd = trainSlice.Last().OpenTime,
                TestStart = testSlice.First().OpenTime, TestEnd = testSlice.Last().OpenTime,
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
}
