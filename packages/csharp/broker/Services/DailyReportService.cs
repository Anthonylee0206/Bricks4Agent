using System.Text;
using System.Text.Json;
using BrokerCore.Contracts;
using BrokerCore.Services;
using BrokerCore.Trading;
using FunctionPool.Registry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Broker.Services;

/// <summary>
/// Daily PnL Report — 每天 UTC 00:00 自動彙總前 24h 交易績效、推 Discord + LINE。
///
/// 來源：trading.account/get_trade_history 純讀本地 DB、含 perp。
/// 算 win_rate / realized_pnl_sum / profit_factor、附 AutoTrader 當前狀態。
///
/// Schedule：
///   - DAILY_REPORT_AT_UTC_HOUR=0 預設、設成 -1 完全關閉自動推（仍可手動觸發 endpoint）
///   - 第一次跑會等到下個整點、避免容器重啟立刻爆訊息
/// </summary>
public class DailyReportService : BackgroundService
{
    private readonly IExecutionDispatcher _dispatcher;
    private readonly IWorkerRegistry _registry;
    private readonly DiscordNotificationService _discord;
    private readonly LineNotificationService _line;
    private readonly AutoTraderService _autoTrader;
    private readonly BrokerCore.Data.BrokerDb _db;
    private readonly NotificationChannelService _notificationChannels;
    private readonly ILogger<DailyReportService> _logger;
    private readonly int _reportHourUtc;
    private readonly string _exchange;

    public DailyReportService(
        IExecutionDispatcher dispatcher,
        IWorkerRegistry registry,
        DiscordNotificationService discord,
        LineNotificationService line,
        AutoTraderService autoTrader,
        BrokerCore.Data.BrokerDb db,
        NotificationChannelService notificationChannels,
        ILogger<DailyReportService> logger)
    {
        _dispatcher = dispatcher;
        _registry = registry;
        _discord = discord;
        _line = line;
        _autoTrader = autoTrader;
        _db = db;
        _notificationChannels = notificationChannels;
        _logger = logger;
        _reportHourUtc = ParseIntEnv("DAILY_REPORT_AT_UTC_HOUR", defaultValue: 0, min: -1, max: 23);
        _exchange = Environment.GetEnvironmentVariable("DAILY_REPORT_EXCHANGE") ?? "bingx";
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_reportHourUtc < 0)
        {
            _logger.LogInformation("DailyReportService disabled (DAILY_REPORT_AT_UTC_HOUR=-1)");
            return;
        }

        _logger.LogInformation("DailyReportService scheduled at UTC {Hour}:00 daily, exchange={Exchange}",
            _reportHourUtc, _exchange);

        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var nextRun = new DateTime(now.Year, now.Month, now.Day, _reportHourUtc, 0, 0, DateTimeKind.Utc);
            if (nextRun <= now) nextRun = nextRun.AddDays(1);
            var delay = nextRun - now;
            _logger.LogDebug("DailyReport: next run at {Next} ({Delay} from now)", nextRun, delay);

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }

            // 每天都跑 24h；週日加跑 weekly (168h)、月初 1 號加跑 monthly (30d ≈ 720h)
            var fired = DateTime.UtcNow;
            try
            {
                await BuildAndPushAsync(periodHours: 24, ct);
                await PushPerUserSummariesAsync(periodHours: 24, ct);   // 多用戶:朋友收自己頻道的彙整

                if (fired.DayOfWeek == DayOfWeek.Sunday)
                {
                    await BuildAndPushAsync(periodHours: 24 * 7, ct);
                    await BuildAndPushScannerShadowAsync(periodHours: 24 * 7, ct);   // 每週 shadow scanner vs backtest 對照
                    await BuildAndPushLiveDivergenceAsync(periodHours: 24 * 90, ct);  // 每週 真錢腿 backtest vs live 背離(僅背離才推)
                }
                if (fired.Day == 1)
                {
                    // 月初 1 號：粗略用 30 天當「上個月」、不用真實天數（差 1-2 天 narrative 可接受）
                    await BuildAndPushAsync(periodHours: 24 * 30, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DailyReport: build/push failed");
            }
        }
    }

    /// <summary>
    /// 組合過去 N 小時的成交摘要 + AutoTrader 狀態、推 Discord + LINE。
    /// 給手動端點 / scheduler 都用。
    /// </summary>
    public async Task<(bool Ok, string Summary)> BuildAndPushAsync(int periodHours, CancellationToken ct, bool push = true)
    {
        var since = DateTime.UtcNow.AddHours(-periodHours);
        var fetched = await FetchTradesAsync(_exchange, since, ct);
        var stats = PnlAggregator.Aggregate(fetched.Select(t => t.Pnl));

        // perp 平倉 income 沒帶 strategy(交易所 income 只給 symbol)→ 用 AutoTrader watchlist 的
        // symbol→策略補上(正規化去 - / 大寫,避免 BNB-USDT vs BNBUSDT 對不上)。
        // 用「當前」對照:同幣日後換策略,舊紀錄歸屬會跟著動(現行穩定配置下可接受)。
        static string Norm(string s) => s.Replace("-", "").Replace("/", "").ToUpperInvariant();
        var symToStrat = _autoTrader.WatchList.Values
            .Where(w => !string.IsNullOrEmpty(w.Strategy))
            .GroupBy(w => Norm(w.Symbol))
            .ToDictionary(g => g.Key, g => g.First().Strategy!);
        string ResolveStrat((decimal Pnl, string? Strategy, string? Symbol) t)
        {
            if (!string.IsNullOrEmpty(t.Strategy)) return t.Strategy!;
            if (!string.IsNullOrEmpty(t.Symbol) && symToStrat.TryGetValue(Norm(t.Symbol!), out var st)) return st;
            return "(無)";
        }

        var byStrategy = fetched
            .GroupBy(ResolveStrat)
            .Select(g => new { Name = g.Key, Stats = PnlAggregator.Aggregate(g.Select(x => x.Pnl)) })
            .OrderByDescending(x => x.Stats.RealizedPnlSum)
            .ToList();

        var watchSnapshot = _autoTrader.WatchList.Values
            .Where(w => w.Active)
            .Select(w => $"{w.Symbol}={w.Strategy} ({w.LastSignal ?? "?"}@{w.LastConfidence:P0})")
            .ToList();

        var pnlSign = stats.RealizedPnlSum >= 0 ? "+" : "";
        var color   = stats.RealizedPnlSum >= 0 ? 0x0ECB81 : 0xF6465D;
        var titleEmoji = stats.RealizedPnlSum >= 0 ? "📈" : "📉";

        var sb = new StringBuilder();
        sb.AppendLine($"**過去 {periodHours}h（{_exchange.ToUpper()}）**");
        sb.AppendLine($"成交筆數：{stats.TradeCount}  勝率：{stats.WinRatePct:F1}%  Profit Factor：{stats.ProfitFactor:F2}");
        sb.AppendLine($"已實現 PnL：**{pnlSign}{stats.RealizedPnlSum:F2}** USDT");
        sb.AppendLine($"勝/敗：{stats.WinCount}/{stats.LoseCount}  平均勝：+{stats.AvgWin:F2}  平均敗：{stats.AvgLoss:F2}");

        if (byStrategy.Count > 0 && byStrategy.Any(x => x.Stats.TradeCount > 0))
        {
            sb.AppendLine();
            sb.AppendLine($"**按策略（{byStrategy.Count} 組）**");
            foreach (var s in byStrategy.Take(6))
            {
                if (s.Stats.TradeCount == 0) continue;
                var sign = s.Stats.RealizedPnlSum >= 0 ? "+" : "";
                sb.AppendLine($"・{s.Name}: {s.Stats.TradeCount}筆 勝率{s.Stats.WinRatePct:F0}% PnL **{sign}{s.Stats.RealizedPnlSum:F2}** PF {s.Stats.ProfitFactor:F2}");
            }
        }

        sb.AppendLine();
        if (periodHours <= 24)
        {
            // 日報:每腿健康 —— 持倉 + 最新 action+原因 + 卡住旗標(解今天「看不到腿為何不跑」的痛點)
            var posSides = FetchPositionSides(_exchange);
            var logBySym = _autoTrader.RecentLogs
                .GroupBy(l => l.Symbol)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.Time).First());
            string Short(string m) => m.Length > 46 ? m[..46] + "…" : m;
            var legs = _autoTrader.WatchList.Values.Where(w => w.Active && w.Exchange == _exchange)
                .OrderByDescending(w => w.BudgetPct).ToList();
            var anomalies = new List<string>();
            sb.AppendLine($"**真錢腿健康 ({legs.Count})**");
            foreach (var w in legs)
            {
                bool hasPos = posSides.TryGetValue(w.Symbol, out var side);
                var sig = w.LastSignal ?? "?";
                logBySym.TryGetValue(w.Symbol, out var lg);
                var reason = lg != null ? $"{lg.Action}: {Short(lg.Message)}" : "—";
                string mark = hasPos ? $"✅ 持倉 {side}" : (sig is "buy" or "sell" ? "⚠ 有訊號未開" : "○ 觀望");
                sb.AppendLine($"・{w.Symbol.Replace("-USDT", "")} {w.Strategy}: {mark} · {sig} — {reason}");
                // 卡住:有 buy/sell 訊號、無持倉、最新動作是 skip/blocked → 異常
                if (!hasPos && sig is "buy" or "sell" && lg != null && lg.Action is "skip" or "blocked" or "error")
                    anomalies.Add($"{w.Symbol.Replace("-USDT", "")}({sig}卡在 {lg.Action})");
            }
            if (anomalies.Count > 0) sb.AppendLine($"⚠ **注意**: {string.Join("、", anomalies)}");
        }
        else
        {
            sb.AppendLine($"**AutoTrader watch ({watchSnapshot.Count} active)**");
            foreach (var w in watchSnapshot.Take(8)) sb.AppendLine($"・{w}");
        }

        var body = sb.ToString();
        var title = $"{titleEmoji} 每日交易彙整 · {DateTime.UtcNow:yyyy-MM-dd}";

        // dry-run(push=false):只回 summary、不推 Discord/LINE → 測試/預覽不洗版
        if (!push)
        {
            _logger.LogInformation("DailyReport dry-run (no push): pnl={Pnl:F2} trades={N}", stats.RealizedPnlSum, stats.TradeCount);
            return (true, body);
        }

        var dr = await _discord.SendAdHocAsync(title, body, color, ct);
        var lr = await _line.SendAdHocAsync(title, body,
            level: stats.RealizedPnlSum >= 0 ? "success" : "warning", ct);

        var ok = dr.ok || lr.ok;
        _logger.LogInformation("DailyReport pushed: discord={D} line={L} pnl={Pnl:F2} trades={N} strategies={S}",
            dr.ok, lr.ok, stats.RealizedPnlSum, stats.TradeCount, byStrategy.Count);
        return (ok, body);
    }

    /// <summary>
    /// 多用戶:對每個有登記 Discord 頻道的「非 admin」用戶,推一份只含他自己成交的精簡每日彙整。
    /// admin(prn_dashboard)已從全域 webhook 收系統總覽、這裡跳過避免重複。
    /// 用 Gap 2 的 owner filter 抓 owner-scoped 成交。回推成功的用戶數。
    /// </summary>
    public async Task<int> PushPerUserSummariesAsync(int periodHours, CancellationToken ct, bool push = true)
    {
        var channels = _notificationChannels.ListActiveByType("discord")
            .Where(c => c.OwnerPrincipalId != "prn_dashboard")   // admin 走系統總覽、不重複
            .GroupBy(c => c.OwnerPrincipalId)
            .Select(g => g.First())                               // 每 owner 取一個 discord 頻道
            .ToList();
        if (channels.Count == 0) return 0;

        var since = DateTime.UtcNow.AddHours(-periodHours);
        int sent = 0;
        foreach (var ch in channels)
        {
            try
            {
                var fetched = await FetchTradesAsync(_exchange, since, ct, ownerPrincipalId: ch.OwnerPrincipalId);
                var stats = PnlAggregator.Aggregate(fetched.Select(t => t.Pnl));
                var byStrategy = fetched
                    .GroupBy(t => string.IsNullOrEmpty(t.Strategy) ? "(無)" : t.Strategy!)
                    .Select(g => new { Name = g.Key, S = PnlAggregator.Aggregate(g.Select(x => x.Pnl)) })
                    .OrderByDescending(x => x.S.RealizedPnlSum)
                    .ToList();

                var pnlSign = stats.RealizedPnlSum >= 0 ? "+" : "";
                var color   = stats.RealizedPnlSum >= 0 ? 0x0ECB81 : 0xF6465D;
                var emoji   = stats.RealizedPnlSum >= 0 ? "📈" : "📉";

                var sb = new StringBuilder();
                sb.AppendLine($"**你的過去 {periodHours}h（{_exchange.ToUpper()}）**");
                sb.AppendLine($"成交筆數：{stats.TradeCount}  勝率：{stats.WinRatePct:F1}%  Profit Factor：{stats.ProfitFactor:F2}");
                sb.AppendLine($"已實現 PnL：**{pnlSign}{stats.RealizedPnlSum:F2}** USDT");
                sb.AppendLine($"勝/敗：{stats.WinCount}/{stats.LoseCount}  平均勝：+{stats.AvgWin:F2}  平均敗：{stats.AvgLoss:F2}");
                if (byStrategy.Any(x => x.S.TradeCount > 0))
                {
                    sb.AppendLine();
                    sb.AppendLine("**按策略**");
                    foreach (var s in byStrategy.Take(6))
                    {
                        if (s.S.TradeCount == 0) continue;
                        var sign = s.S.RealizedPnlSum >= 0 ? "+" : "";
                        sb.AppendLine($"・{s.Name}: {s.S.TradeCount}筆 勝率{s.S.WinRatePct:F0}% PnL **{sign}{s.S.RealizedPnlSum:F2}**");
                    }
                }
                var title = $"{emoji} 你的每日交易彙整 · {DateTime.UtcNow:yyyy-MM-dd}";

                if (!push) continue;
                var (sok, err) = await _discord.SendAdHocToWebhookAsync(ch.Target, title, sb.ToString(), color, ct);
                if (sok) sent++;
                else _logger.LogWarning("PerUser daily report push failed for {Owner}: {Err}", ch.OwnerPrincipalId, err);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PerUser daily report failed for {Owner}", ch.OwnerPrincipalId);
            }
        }
        _logger.LogInformation("PerUser daily reports pushed: {Sent}/{Total}", sent, channels.Count);
        return sent;
    }

    /// <summary>
    /// 每週 shadow scanner vs backtest 對照(2026-05-29)— 累積 closed leg 統計 + 本期增量,跟 backtest 預期勝率對照、標偏離。
    /// 目標:4 週影子期累積後判斷哪條 live 復現 backtest、可升真錢。純讀 scanner_active_legs。
    /// </summary>
    public async Task<(bool Ok, string Summary)> BuildAndPushScannerShadowAsync(int periodHours, CancellationToken ct, bool push = true)
    {
        await Task.CompletedTask;
        // backtest 預期勝率(strat-validate 跨幣中位、含 2022)— 偏離參考線
        var expWr = new Dictionary<string, decimal>
        {
            ["retail_ls_contrarian_tight"] = 50m, ["retail_ls_delta_contrarian"] = 46m,
            ["funding_momentum_ls"] = 42m, ["fundmom_ls_xtight"] = 47m,
            ["ts_momentum"] = 44m, ["tsmom_widepz"] = 46m, ["decorr5_scan10"] = 49m,
            ["harm_prz_scan10"] = 42m, ["harm_prz_scan10_widepz"] = 50m, ["harm_prz_top2_scan10_widepz"] = 48m,
        };

        List<BrokerCore.Models.ScannerLegEntry> scanners;
        try { scanners = _db.Query<BrokerCore.Models.ScannerLegEntry>("SELECT * FROM scanner_legs WHERE enabled = 1 ORDER BY id"); }
        catch (Exception ex) { _logger.LogWarning(ex, "ScannerShadow: scanner_legs query failed"); return (false, "query failed"); }
        if (scanners.Count == 0) return (false, "no enabled scanners");

        var since = DateTime.UtcNow.AddHours(-periodHours);
        var sb = new StringBuilder();
        sb.AppendLine($"**Shadow Scanner 表現 vs backtest（{scanners.Count} 條 enabled）**");
        sb.AppendLine($"_累積 closed 統計 + 近 {periodHours / 24}d 增量;偏離 = live 勝率 − backtest 預期_");
        sb.AppendLine();

        int totalClosed = 0, totalOpen = 0;
        foreach (var sc in scanners)
        {
            List<BrokerCore.Models.ScannerActiveLegEntry> legs;
            try { legs = _db.Query<BrokerCore.Models.ScannerActiveLegEntry>(
                "SELECT * FROM scanner_active_legs WHERE scanner_id = @s", new { s = sc.Id }); }
            catch { continue; }

            var closed = legs.Where(l => l.ClosedAt != null).ToList();
            int openCnt = legs.Count(l => l.ClosedAt == null);
            int newOpen = legs.Count(l => l.OpenedAt >= since);
            int newClose = closed.Count(l => l.ClosedAt >= since);
            totalClosed += closed.Count; totalOpen += openCnt;

            if (closed.Count == 0)
            {
                sb.AppendLine($"・**{sc.Strategy}**: 0 closed · {openCnt} open · 本期 +{newOpen}開 (資料未足)");
                continue;
            }
            int wins = closed.Count(l => l.RealizedPnlPct > 0);
            decimal wr = (decimal)wins / closed.Count * 100m;
            decimal cumPnl = closed.Sum(l => l.RealizedPnlPct);
            double avgHold = closed.Average(l => ((l.ClosedAt!.Value) - l.OpenedAt).TotalDays);
            string dev = "";
            if (expWr.TryGetValue(sc.Strategy, out var ew) && closed.Count >= 5)
            {
                var d = wr - ew;
                dev = $" · vs backtest {ew:F0}%: {(d >= 0 ? "+" : "")}{d:F0}pp{(Math.Abs(d) > 20 ? " ⚠偏離" : "")}";
            }
            sb.AppendLine($"・**{sc.Strategy}**: {closed.Count}closed 勝率{wr:F0}% 累計{(cumPnl >= 0 ? "+" : "")}{cumPnl:F1}% 持{avgHold:F0}d · {openCnt}open 本期+{newOpen}/-{newClose}{dev}");
        }
        sb.AppendLine();
        sb.AppendLine($"合計 {totalClosed} closed · {totalOpen} open。樣本 <5 closed 不算勝率(影子期初常見)。");

        // ── VRP / 波動 carry shadow(2026-06-16、獨立於方向性 scanner)── 裸/封頂雙 PnL 對照 ──
        try
        {
            var vrpLegs = _db.Query<BrokerCore.Models.VrpShadowLegEntry>("SELECT * FROM vrp_shadow_legs");
            var vrpSettled = vrpLegs.Where(l => l.SettledAt != null).ToList();
            int vrpOpen = vrpLegs.Count(l => l.SettledAt == null);
            int vrpNew = vrpLegs.Count(l => l.CreatedAt >= since);
            sb.AppendLine();
            if (vrpSettled.Count == 0)
            {
                sb.AppendLine($"**VRP 波動 carry shadow（BTC）**: 0 settled · {vrpOpen} open · 本期 +{vrpNew}開 (資料未足)");
            }
            else
            {
                int vrpWins = vrpSettled.Count(l => l.PnlPctCapped > 0);
                decimal vrpWr = (decimal)vrpWins / vrpSettled.Count * 100m;
                decimal avgNaked = vrpSettled.Average(l => l.PnlPctNaked);
                decimal avgCapped = vrpSettled.Average(l => l.PnlPctCapped);
                decimal cumCapped = vrpSettled.Sum(l => l.PnlPctCapped);
                decimal worstNaked = vrpSettled.Min(l => l.PnlPctNaked);
                decimal worstCapped = vrpSettled.Min(l => l.PnlPctCapped);
                decimal avgImplied = vrpSettled.Average(l => l.DvolEntry);
                decimal avgRealized = vrpSettled.Average(l => l.RealizedVol);
                sb.AppendLine($"**VRP 波動 carry shadow（BTC）**: {vrpSettled.Count}settled 勝率{vrpWr:F0}% · {vrpOpen}open 本期+{vrpNew}");
                sb.AppendLine($"　封頂累計{(cumCapped >= 0 ? "+" : "")}{cumCapped:F1}% 均{avgCapped:F1}%/窗(最差{worstCapped:F0}%) vs 裸賣均{avgNaked:F1}%(最差{worstNaked:F0}%)→ 封頂救左尾");
                sb.AppendLine($"　隱含均{avgImplied:F1} vs 實現均{avgRealized:F1}(差>0 = 賣方收到溢酬)");
            }
        }
        catch (Exception ex) { _logger.LogDebug(ex, "VRP shadow report section skipped (likely uninitialized)"); }

        var body = sb.ToString();
        var title = $"🔬 Shadow Scanner 週報 · {DateTime.UtcNow:yyyy-MM-dd}";
        if (!push) return (true, body);
        var dr = await _discord.SendAdHocAsync(title, body, 0x3a8bfd, ct);
        _logger.LogInformation("ScannerShadow pushed: discord={D} scanners={N} closed={C}", dr.ok, scanners.Count, totalClosed);
        return (dr.ok, body);
    }

    /// <summary>
    /// 真錢腿 backtest vs live 背離監控(2026-06-03)。對每個 budget_pct>0 的真錢策略:
    /// 部署時 backtest 為正 edge、但 live 累計虧損(≥minTrades 筆)= 背離 → 推 Discord ⚠ 警報。
    /// 健康時不推、不洗版(只在偵測到背離才告警)。觸發信號用「live 累計 PnL &lt; 0」而非勝率
    /// (趨勢策略本就低勝率高賺賠比、用勝率會誤報)。
    /// dual_mom 案例(backtest t3.66 強、live 0/3 虧 −13.5)會被自動抓到——之前靠人肉審計才發現。
    /// 真錢調整(減碼/下架)仍是人工;此監控只負責「叫」。
    /// </summary>
    public async Task<(bool Ok, string Summary, int Diverged)> BuildAndPushLiveDivergenceAsync(
        int periodHours, CancellationToken ct, bool push = true)
    {
        const int minTrades = 3;   // 真錢腿成交稀疏、低門檻;小樣本只當「heads-up」非定論
        List<BrokerCore.Models.AutoTradeWatchEntry> live;
        try
        {
            live = _db.Query<BrokerCore.Models.AutoTradeWatchEntry>(
                "SELECT * FROM auto_trade_watchlist WHERE active = 1 AND budget_pct > 0 ORDER BY budget_pct DESC");
        }
        catch (Exception ex) { _logger.LogWarning(ex, "LiveDivergence: watchlist query failed"); return (false, "query failed", 0); }
        if (live.Count == 0) return (true, "no real-money legs", 0);

        var since = DateTime.UtcNow.AddHours(-periodHours);
        var trades = await FetchTradesAsync(_exchange, since, ct);
        var byStrat = trades.Where(t => t.Strategy != null)
            .GroupBy(t => t.Strategy!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var diverged = new List<string>();
        foreach (var leg in live)
        {
            byStrat.TryGetValue(leg.Strategy, out var g);
            int n = g?.Count ?? 0;
            if (n < minTrades) continue;   // 樣本不足、不判(真錢腿初期常見)
            int wins = g!.Count(t => t.Pnl > 0);
            decimal wr = (decimal)wins / n * 100m;
            decimal cumPnl = g!.Sum(t => t.Pnl);
            if (cumPnl < 0m)   // 部署=正 edge 卻 live 累計虧 = 背離
                diverged.Add($"**{leg.Strategy}**（{leg.Symbol} 配重{leg.BudgetPct:F0}%）：{n} 筆 勝率{wr:F0}% 累計 {cumPnl:F1}");
        }

        if (diverged.Count == 0)
        {
            _logger.LogInformation("LiveDivergence: {N} real-money legs checked, no divergence", live.Count);
            return (true, "no divergence", 0);   // 健康 → 不推
        }

        var alert = new StringBuilder();
        alert.AppendLine($"**真錢腿 backtest vs live 背離 — {diverged.Count} 條偏離**");
        alert.AppendLine("_部署時 backtest 為正 edge、live 卻累計虧損 → 考慮減碼/下架(真錢調整=人工)_");
        alert.AppendLine();
        foreach (var d in diverged) alert.AppendLine($"・{d}");
        alert.AppendLine();
        alert.AppendLine($"_近 {periodHours / 24}d、≥{minTrades} 筆才判;小樣本當 heads-up 非定論_");

        var body = alert.ToString();
        if (!push) return (true, body, diverged.Count);
        var dr = await _discord.SendAdHocAsync($"⚠ 真錢背離警報 · {DateTime.UtcNow:yyyy-MM-dd}", body, 0xff3b30, ct);
        _logger.LogWarning("LiveDivergence: {N} legs DIVERGED, pushed discord={D}", diverged.Count, dr.ok);
        return (dr.ok, body, diverged.Count);
    }

    private async Task<List<(decimal Pnl, string? Strategy, string? Symbol)>> FetchTradesAsync(
        string exchange, DateTime sinceUtc, CancellationToken ct, string? ownerPrincipalId = null)
    {
        var empty = new List<(decimal, string?, string?)>();
        if (!_registry.HasAvailableWorker("trading.account")) return empty;

        // ownerPrincipalId 非空 → get_trade_history 只回該 owner 的成交(每用戶彙整用);
        // null → 系統總覽(operator 日報、看全部)。
        var payload = JsonSerializer.Serialize(new
        {
            exchange,
            limit = 500,
            since = sinceUtc.ToString("o"),
            owner_principal_id = ownerPrincipalId,
        });
        var req = new ApprovedRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            CapabilityId = "trading.account",
            Route = "get_trade_history",
            Payload = payload,
            Scope = "{}",
            PrincipalId = "system",   // 內部排程、不過 ACL
            TaskId = "daily-report",
            SessionId = "daily-report",
        };
        var result = await _dispatcher.DispatchAsync(req);
        if (!result.Success) return empty;

        var doc = JsonDocument.Parse(result.ResultPayload ?? "{}").RootElement;
        if (!doc.TryGetProperty("trades", out var trades) || trades.ValueKind != JsonValueKind.Array)
            return empty;

        var list = new List<(decimal, string?, string?)>();
        foreach (var t in trades.EnumerateArray())
        {
            if (!t.TryGetProperty("realized_pnl", out var p) || p.ValueKind != JsonValueKind.Number) continue;
            var pnl = p.GetDecimal();
            var strategy = t.TryGetProperty("strategy", out var sg) && sg.ValueKind == JsonValueKind.String
                ? sg.GetString() : null;
            var symbol = t.TryGetProperty("symbol", out var sy) && sy.ValueKind == JsonValueKind.String
                ? sy.GetString() : null;
            list.Add((pnl, strategy, symbol));
        }
        return list;
    }

    // 目前有倉的 symbol→side。讀 broker 自己的 perp_position_state(get_positions 對 bingx perp
    // 走 trading.account 會回空;perp_position_state 是保護掃描維護的可靠快取)。失敗回空、不擋報告。
    private Dictionary<string, string> FetchPositionSides(string exchange)
    {
        var outp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = _db.Query<BrokerCore.Models.PerpetualPositionStateEntry>(
                "SELECT * FROM perp_position_state WHERE exchange = @ex", new { ex = exchange });
            foreach (var r in rows)
                if (!string.IsNullOrEmpty(r.Symbol)) outp[r.Symbol] = r.Side;
        }
        catch { }
        return outp;
    }

    private static int ParseIntEnv(string name, int defaultValue, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (int.TryParse(raw, out var v) && v >= min && v <= max) return v;
        return defaultValue;
    }
}
