using Microsoft.Extensions.Logging;
using QuoteWorker.Storage;

namespace QuoteWorker.History;

/// <summary>
/// 啟動時自動抓取所有已設定 symbol 的歷史 K 線。
/// 只抓增量（DB 中已有的不會重複抓）。
/// 同時支援批次手動觸發。
/// </summary>
public class StartupHistoryFetcher
{
    private readonly HistoricalDataFetcher _fetcher;
    private readonly QuoteDbStorage _db;
    private readonly ILogger<StartupHistoryFetcher> _logger;
    private readonly string[] _stockSymbols;
    private readonly string[] _cryptoIds;
    private readonly string[] _backfillSymbols;   // Binance 符號（"BTCUSDT"…）；空 → 從 _cryptoIds 推導

    // 深度回補目標根數（per interval）。CountBars 已 ≥ 目標就 skip、restart 不重抓。
    private static readonly (string Interval, int Target)[] DeepTargets =
    {
        ("15m", 2000),  // ~21 天（短樣本、回測會自動受限於可得根數）
        ("30m", 2000),  // ~42 天
        ("1h", 2000),   // ~83 天
        ("2h", 2000),   // ~166 天
        ("4h", 1500),   // ~250 天
        ("1d", 1500),   // ~4 年日線
        ("3d", 1000),   // ~8 年（Binance 上線後全段）
        ("1w", 600),    // 全段週線（幣種上線時間有限、抓得到多少算多少）
    };

    // 資金費率回補目標筆數（8h 一次 → 1000 筆 ≈ 333 天)。
    private const int FundingTarget = 1000;

    // Deribit DVOL 隱含波動回補目標天數(~4 年、涵蓋 2022 熊市;BTC DVOL 2021-03 上線)。
    // strategy 側 VRP 只用 BTC,ETH 一併抓(便宜、留作日後 cross-section、達標即 skip)。
    private const int DvolTarget = 1500;
    private static readonly string[] DvolCurrencies = { "BTC", "ETH" };

    public bool IsFetching { get; private set; }
    public string LastStatus { get; private set; } = "idle";

    public StartupHistoryFetcher(
        HistoricalDataFetcher fetcher,
        QuoteDbStorage db,
        ILogger<StartupHistoryFetcher> logger,
        string stockSymbols,
        string cryptoIds,
        string backfillSymbols = "")
    {
        _fetcher      = fetcher;
        _db           = db;
        _logger       = logger;
        _stockSymbols = stockSymbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _cryptoIds    = cryptoIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _backfillSymbols = backfillSymbols.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>啟動後延遲執行一次完整歷史抓取。</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        // 等 10 秒讓 worker 先連上 broker
        await Task.Delay(TimeSpan.FromSeconds(10), ct).ContinueWith(_ => { });

        _logger.LogInformation("StartupHistoryFetcher: begin initial fetch for {Stocks} stocks, {Crypto} crypto",
            _stockSymbols.Length, _cryptoIds.Length);

        await FetchAllAsync(ct);
    }

    /// <summary>批次抓取所有 symbol 的所有時間框架。</summary>
    public async Task<(int totalBars, List<string> errors)> FetchAllAsync(CancellationToken ct = default)
    {
        if (IsFetching) return (0, new List<string> { "Already fetching" });

        IsFetching = true;
        LastStatus = "fetching";
        int totalBars = 0;
        var errors = new List<string>();

        // 美股
        foreach (var symbol in _stockSymbols)
        {
            if (ct.IsCancellationRequested) break;
            foreach (var interval in new[] { "1d" }) // Yahoo 只支援日線
            {
                try
                {
                    var count = await _fetcher.FetchStockHistoryAsync(symbol, "2y", interval, ct);
                    totalBars += count;
                    _logger.LogInformation("History: {Symbol} {Interval} → {Count} bars", symbol, interval, count);
                    await Task.Delay(500, ct).ContinueWith(_ => { }); // rate limit
                }
                catch (Exception ex)
                {
                    errors.Add($"{symbol}/{interval}: {ex.Message}");
                    _logger.LogWarning(ex, "History fetch failed: {Symbol} {Interval}", symbol, interval);
                }
            }
        }

        // 加密貨幣深度回補（Binance）：用 backfill 符號清單（沒設就從 _cryptoIds 推導 binance 符號）。
        // 每個 symbol×interval 分頁抓到目標根數；已達標就 skip、restart 不重抓（idempotent upsert）。
        var cryptoSymbols = _backfillSymbols.Length > 0
            ? _backfillSymbols
            : _cryptoIds.Select(HistoricalDataFetcher.CoinGeckoToBinance).ToArray();

        foreach (var binanceSymbol in cryptoSymbols)
        {
            if (ct.IsCancellationRequested) break;
            var baseSymbol = binanceSymbol.Replace("USDT", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
            foreach (var (interval, target) in DeepTargets)
            {
                try
                {
                    var have = _db.CountBars(baseSymbol, interval);
                    if (have >= target)
                    {
                        _logger.LogInformation("History deep skip {Symbol} {Interval}: already {Have} ≥ {Target}",
                            binanceSymbol, interval, have, target);
                        continue;
                    }
                    var count = await _fetcher.FetchCryptoDeepAsync(binanceSymbol, interval, target, ct);
                    totalBars += count;
                    _logger.LogInformation("History deep: {Symbol} {Interval} → {Count} bars (had {Have}, target {Target})",
                        binanceSymbol, interval, count, have, target);
                    await Task.Delay(300, ct).ContinueWith(_ => { });
                }
                catch (Exception ex)
                {
                    errors.Add($"{binanceSymbol}/{interval}: {ex.Message}");
                    _logger.LogWarning(ex, "History deep fetch failed: {Symbol} {Interval}", binanceSymbol, interval);
                }
            }
        }

        // 資金費率深度回補（非價格因子、可回測):每幣抓 ~FundingTarget 筆(8h 一次 → ~333 天)。
        // 已達標就 skip。OI 不在這回補(history 只 ~30 天、改走 get_oi_now 即時)。
        foreach (var binanceSymbol in cryptoSymbols)
        {
            if (ct.IsCancellationRequested) break;
            var baseSymbol = binanceSymbol.Replace("USDT", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant();
            try
            {
                var have = _db.CountFundingRates(baseSymbol);
                if (have >= FundingTarget)
                {
                    _logger.LogInformation("Funding deep skip {Symbol}: already {Have} ≥ {Target}",
                        binanceSymbol, have, FundingTarget);
                    continue;
                }
                var count = await _fetcher.FetchFundingRateDeepAsync(binanceSymbol, FundingTarget, ct);
                _logger.LogInformation("Funding deep: {Symbol} → {Count} points (had {Have}, target {Target})",
                    binanceSymbol, count, have, FundingTarget);
                await Task.Delay(300, ct).ContinueWith(_ => { });
            }
            catch (Exception ex)
            {
                errors.Add($"{binanceSymbol}/funding: {ex.Message}");
                _logger.LogWarning(ex, "Funding deep fetch failed: {Symbol}", binanceSymbol);
            }
        }

        // Q2 retail L/S 抓最近 30 天(strategy 100 lookback、長期累積夠 + tools/seed-retail-ls 一次性 backfill)
        foreach (var binanceSymbol in cryptoSymbols)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var count = await _fetcher.FetchRetailLsDeepAsync(binanceSymbol, ct);
                if (count > 0)
                    _logger.LogInformation("Retail L/S: {Symbol} → {Count} daily points", binanceSymbol, count);
                await Task.Delay(300, ct).ContinueWith(_ => { });
            }
            catch (Exception ex)
            {
                errors.Add($"{binanceSymbol}/retail_ls: {ex.Message}");
                _logger.LogWarning(ex, "Retail L/S fetch failed: {Symbol}", binanceSymbol);
            }
        }

        // Q2 OI 歷史抓最近 30 天(oi_contrarian;同 retail_ls 累積策略 + seed 工具 backfill 歷史)
        foreach (var binanceSymbol in cryptoSymbols)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var count = await _fetcher.FetchOiHistDeepAsync(binanceSymbol, ct);
                if (count > 0)
                    _logger.LogInformation("OI hist: {Symbol} → {Count} daily points", binanceSymbol, count);
                await Task.Delay(300, ct).ContinueWith(_ => { });
            }
            catch (Exception ex)
            {
                errors.Add($"{binanceSymbol}/oi_hist: {ex.Message}");
                _logger.LogWarning(ex, "OI hist fetch failed: {Symbol}", binanceSymbol);
            }
        }

        // VRP / 波動 carry:Deribit DVOL 隱含波動指數深度回補(BTC/ETH、日線、免金鑰)。達標即 skip。
        foreach (var cur in DvolCurrencies)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var have = _db.CountDvolValues(cur);
                if (have >= DvolTarget)
                {
                    _logger.LogInformation("DVOL deep skip {Cur}: already {Have} ≥ {Target}", cur, have, DvolTarget);
                    continue;
                }
                var count = await _fetcher.FetchDvolDeepAsync(cur, DvolTarget, ct);
                _logger.LogInformation("DVOL deep: {Cur} → {Count} daily points (had {Have}, target {Target})",
                    cur, count, have, DvolTarget);
                await Task.Delay(300, ct).ContinueWith(_ => { });
            }
            catch (Exception ex)
            {
                errors.Add($"{cur}/dvol: {ex.Message}");
                _logger.LogWarning(ex, "DVOL deep fetch failed: {Cur}", cur);
            }
        }

        IsFetching = false;
        LastStatus = $"done: {totalBars} bars, {errors.Count} errors";
        _logger.LogInformation("StartupHistoryFetcher complete: {Total} bars, {Errors} errors", totalBars, errors.Count);
        return (totalBars, errors);
    }
}
