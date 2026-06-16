using System.Text.Json;
using Microsoft.Extensions.Logging;
using QuoteWorker.Models;
using QuoteWorker.Storage;

namespace QuoteWorker.History;

/// <summary>
/// 歷史 K 線抓取器。
/// - 美股：Yahoo Finance chart API（最多 2 年日 K）
/// - 加密貨幣：Binance public klines API（最多 1000 根 bar）
/// 支援增量抓取（從 DB 最新一根 bar 之後開始）。
/// </summary>
public class HistoricalDataFetcher
{
    private readonly HttpClient _http;
    private readonly QuoteDbStorage _db;
    private readonly ILogger<HistoricalDataFetcher> _logger;

    public HistoricalDataFetcher(
        HttpClient http,
        QuoteDbStorage db,
        ILogger<HistoricalDataFetcher> logger)
    {
        _http   = http;
        _db     = db;
        _logger = logger;
    }

    // ── 美股：Yahoo Finance ──────────────────────────────────────────

    /// <summary>
    /// 抓取美股歷史日 K。
    /// range: "1mo","3mo","6mo","1y","2y","5y","max"
    /// </summary>
    public async Task<int> FetchStockHistoryAsync(
        string symbol, string range = "2y", string interval = "1d", CancellationToken ct = default)
    {
        var latestBar = _db.GetLatestBarTime(symbol, interval);
        var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{symbol}" +
                  $"?interval={interval}&range={range}";

        _logger.LogInformation("Fetching stock history: {Symbol} range={Range} interval={Interval}", symbol, range, interval);

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Yahoo Finance returned {Code} for {Symbol}", resp.StatusCode, symbol);
            return 0;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var chart = doc.RootElement.GetProperty("chart");
        if (!chart.TryGetProperty("result", out var resultArr) || resultArr.GetArrayLength() == 0)
            return 0;

        var result    = resultArr[0];
        var timestamps = result.GetProperty("timestamp");
        var ohlcv      = result.GetProperty("indicators").GetProperty("quote")[0];

        var opens   = ohlcv.GetProperty("open");
        var highs   = ohlcv.GetProperty("high");
        var lows    = ohlcv.GetProperty("low");
        var closes  = ohlcv.GetProperty("close");
        var volumes = ohlcv.GetProperty("volume");

        var bars = new List<OhlcvBar>();
        for (int i = 0; i < timestamps.GetArrayLength(); i++)
        {
            if (opens[i].ValueKind == JsonValueKind.Null ||
                closes[i].ValueKind == JsonValueKind.Null)
                continue;

            var openTime = DateTimeOffset.FromUnixTimeSeconds(timestamps[i].GetInt64()).UtcDateTime;

            // 增量：跳過已有的 bar
            if (latestBar.HasValue && openTime <= latestBar.Value)
                continue;

            bars.Add(new OhlcvBar
            {
                Symbol    = symbol,
                Type      = symbol.EndsWith(".TW", StringComparison.OrdinalIgnoreCase) ? "tw_stock" : "stock",
                Interval  = interval,
                OpenTime  = openTime,
                CloseTime = openTime.Date.AddDays(1).AddSeconds(-1),
                Open      = GetDecimal(opens[i]),
                High      = GetDecimal(highs[i]),
                Low       = GetDecimal(lows[i]),
                Close     = GetDecimal(closes[i]),
                Volume    = volumes[i].ValueKind != JsonValueKind.Null ? GetDecimal(volumes[i]) : 0,
            });
        }

        if (bars.Count > 0)
            _db.SaveBars(bars);

        _logger.LogInformation("Stock history {Symbol}: saved {Count} new bars (total range: {Range})",
            symbol, bars.Count, range);
        return bars.Count;
    }

    // ── 加密貨幣：Binance ────────────────────────────────────────────

    /// <summary>
    /// 抓取加密貨幣歷史 K 線。
    /// binanceSymbol: "BTCUSDT","ETHUSDT" 等。
    /// interval: "1m","5m","15m","1h","4h","1d","1w"
    /// </summary>
    public async Task<int> FetchCryptoHistoryAsync(
        string binanceSymbol, string interval = "1d", int limit = 365, CancellationToken ct = default)
    {
        var normalizedSymbol = binanceSymbol.Replace("USDT", "").ToUpper();
        var latestBar = _db.GetLatestBarTime(normalizedSymbol, interval);

        var url = $"https://api.binance.com/api/v3/klines" +
                  $"?symbol={binanceSymbol}&interval={interval}&limit={limit}";

        // 增量抓取：從最新 bar 之後開始
        if (latestBar.HasValue)
        {
            var startMs = new DateTimeOffset(latestBar.Value).ToUnixTimeMilliseconds() + 1;
            url += $"&startTime={startMs}";
        }

        _logger.LogInformation("Fetching crypto history: {Symbol} interval={Interval} limit={Limit}",
            binanceSymbol, interval, limit);

        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Binance returned {Code} for {Symbol}", resp.StatusCode, binanceSymbol);
            return 0;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var bars = new List<OhlcvBar>();
        foreach (var kline in doc.RootElement.EnumerateArray())
        {
            // Binance kline format: [openTime, open, high, low, close, volume, closeTime, ...]
            var openTime  = DateTimeOffset.FromUnixTimeMilliseconds(kline[0].GetInt64()).UtcDateTime;
            var closeTime = DateTimeOffset.FromUnixTimeMilliseconds(kline[6].GetInt64()).UtcDateTime;

            bars.Add(new OhlcvBar
            {
                Symbol    = normalizedSymbol,
                Type      = "crypto",
                Interval  = interval,
                OpenTime  = openTime,
                CloseTime = closeTime,
                Open      = decimal.Parse(kline[1].GetString()!),
                High      = decimal.Parse(kline[2].GetString()!),
                Low       = decimal.Parse(kline[3].GetString()!),
                Close     = decimal.Parse(kline[4].GetString()!),
                Volume    = decimal.Parse(kline[5].GetString()!),
            });
        }

        if (bars.Count > 0)
            _db.SaveBars(bars);

        _logger.LogInformation("Crypto history {Symbol}: saved {Count} new bars", binanceSymbol, bars.Count);
        return bars.Count;
    }

    /// <summary>
    /// 深度回補：分頁抓「過去 targetBars 根」到現在（Binance 單次上限 1000、這裡 startTime 往前推、
    /// 一頁一頁 forward 抓到現在）。用於一次性把歷史補深（既有的 forward-incremental 只能往後加新 bar、
    /// 補不了更早的歷史）。SaveBars 走 PK upsert、重疊範圍重跑不會重複。
    /// </summary>
    public async Task<int> FetchCryptoDeepAsync(
        string binanceSymbol, string interval, int targetBars, CancellationToken ct = default)
    {
        var normalizedSymbol = binanceSymbol.Replace("USDT", "").ToUpper();
        var intervalMs = IntervalToMs(interval);
        if (intervalMs <= 0)
        {
            _logger.LogWarning("Deep fetch: unsupported interval {Interval}", interval);
            return 0;
        }

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long startMs = nowMs - (long)targetBars * intervalMs;
        int totalSaved = 0;
        int maxPages = targetBars / 1000 + 2;  // 安全上限、避免無限迴圈

        for (int page = 0; page < maxPages; page++)
        {
            if (ct.IsCancellationRequested) break;

            var url = $"https://api.binance.com/api/v3/klines" +
                      $"?symbol={binanceSymbol}&interval={interval}&limit=1000&startTime={startMs}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance deep returned {Code} for {Symbol} {Interval} page {Page}",
                    resp.StatusCode, binanceSymbol, interval, page);
                break;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            int len = arr.GetArrayLength();
            if (len == 0) break;

            var bars = new List<OhlcvBar>(len);
            long lastOpen = startMs;
            foreach (var kline in arr.EnumerateArray())
            {
                var openMs = kline[0].GetInt64();
                lastOpen = openMs;
                bars.Add(new OhlcvBar
                {
                    Symbol    = normalizedSymbol,
                    Type      = "crypto",
                    Interval  = interval,
                    OpenTime  = DateTimeOffset.FromUnixTimeMilliseconds(openMs).UtcDateTime,
                    CloseTime = DateTimeOffset.FromUnixTimeMilliseconds(kline[6].GetInt64()).UtcDateTime,
                    Open      = decimal.Parse(kline[1].GetString()!),
                    High      = decimal.Parse(kline[2].GetString()!),
                    Low       = decimal.Parse(kline[3].GetString()!),
                    Close     = decimal.Parse(kline[4].GetString()!),
                    Volume    = decimal.Parse(kline[5].GetString()!),
                });
            }
            _db.SaveBars(bars);
            totalSaved += bars.Count;

            if (len < 1000) break;                          // 已抓到最新、沒有下一頁
            startMs = lastOpen + intervalMs;                // 下一頁從上頁最後一根之後開始
            if (startMs >= nowMs) break;
            await Task.Delay(250, ct).ContinueWith(_ => { }); // 輕量 rate limit
        }

        _logger.LogInformation("Crypto deep {Symbol} {Interval}: saved {Count} bars (target {Target})",
            binanceSymbol, interval, totalSaved, targetBars);
        return totalSaved;
    }

    // ── 永續資金費率（非價格因子）──────────────────────────────────

    /// <summary>
    /// 深度回補資金費率：分頁抓「過去 targetPoints 筆」到現在。
    /// Binance fapi/v1/fundingRate 單次上限 1000、用 startTime 一頁頁往後抓。
    /// SaveFundingRates 走 (symbol, funding_time) PK upsert、重疊重跑不重複。
    /// </summary>
    public async Task<int> FetchFundingRateDeepAsync(
        string binanceSymbol, int targetPoints, CancellationToken ct = default)
    {
        var normalizedSymbol = binanceSymbol.Replace("USDT", "").ToUpper();
        // 多數 symbol 8h 一次 → 用 8h 估起始時間;抓不到更早的就自然停（len<1000）。
        const long fundingIntervalMs = 8 * 3_600_000L;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long startMs = nowMs - (long)targetPoints * fundingIntervalMs;
        int totalSaved = 0;
        int maxPages = targetPoints / 1000 + 2;

        for (int page = 0; page < maxPages; page++)
        {
            if (ct.IsCancellationRequested) break;

            var url = $"https://fapi.binance.com/fapi/v1/fundingRate" +
                      $"?symbol={binanceSymbol}&limit=1000&startTime={startMs}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance funding returned {Code} for {Symbol} page {Page}",
                    resp.StatusCode, binanceSymbol, page);
                break;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            int len = arr.GetArrayLength();
            if (len == 0) break;

            var points = new List<FundingRatePoint>(len);
            long lastTime = startMs;
            foreach (var el in arr.EnumerateArray())
            {
                var ft = el.GetProperty("fundingTime").GetInt64();
                lastTime = ft;
                if (!decimal.TryParse(el.GetProperty("fundingRate").GetString(), out var rate)) continue;
                points.Add(new FundingRatePoint
                {
                    Symbol      = normalizedSymbol,
                    FundingTime = DateTimeOffset.FromUnixTimeMilliseconds(ft).UtcDateTime,
                    FundingRate = rate,
                });
            }
            _db.SaveFundingRates(points);
            totalSaved += points.Count;

            if (len < 1000) break;
            startMs = lastTime + 1;
            if (startMs >= nowMs) break;
            await Task.Delay(250, ct).ContinueWith(_ => { });
        }

        _logger.LogInformation("Funding deep {Symbol}: saved {Count} points (target {Target})",
            binanceSymbol, totalSaved, targetPoints);
        return totalSaved;
    }

    /// <summary>
    /// 散戶多空比深度回補(Q2 retail_ls_contrarian alpha 來源)。
    /// Binance /futures/data/globalLongShortAccountRatio:period=1d、limit max 30。
    /// 公開 API 只回 30 天、歷史靠 tools/oi-validate 從 data.binance.vision 一次性 seed。
    /// 此 runtime 路徑保證每天有最新值,長期累積成 100+ 天 lookback。
    /// </summary>
    public async Task<int> FetchRetailLsDeepAsync(
        string binanceSymbol, CancellationToken ct = default)
    {
        var normalizedSymbol = binanceSymbol.Replace("USDT", "").ToUpper();
        var url = $"https://fapi.binance.com/futures/data/globalLongShortAccountRatio" +
                  $"?symbol={binanceSymbol}&period=1d&limit=30";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance retail L/S returned {Code} for {Symbol}",
                    resp.StatusCode, binanceSymbol);
                return 0;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var points = new List<RetailLsRatioPoint>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("longShortRatio", out var rEl)) continue;
                if (!decimal.TryParse(rEl.GetString(), out var ratio)) continue;
                if (!el.TryGetProperty("timestamp", out var tEl)) continue;
                long ts = tEl.GetInt64();
                points.Add(new RetailLsRatioPoint
                {
                    Symbol = normalizedSymbol,
                    SampleTime = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime,
                    LsRatio = ratio,
                });
            }
            _db.SaveRetailLsRatios(points);
            _logger.LogInformation("Retail L/S deep {Symbol}: saved {Count} points (30d API)",
                binanceSymbol, points.Count);
            return points.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retail L/S deep fetch failed: {Symbol}", binanceSymbol);
            return 0;
        }
    }

    /// <summary>
    /// 未平倉量歷史深度回補(Q2 oi_contrarian alpha)。
    /// Binance /futures/data/openInterestHist:period=1d、limit max 30、回 sumOpenInterestValue(USDT 名目)。
    /// 公開 API 只回 30 天、歷史靠 tools/seed-retail-ls(同時 seed retail_ls + OI)一次性 backfill。
    /// </summary>
    public async Task<int> FetchOiHistDeepAsync(
        string binanceSymbol, CancellationToken ct = default)
    {
        var normalizedSymbol = binanceSymbol.Replace("USDT", "").ToUpper();
        var url = $"https://fapi.binance.com/futures/data/openInterestHist" +
                  $"?symbol={binanceSymbol}&period=1d&limit=30";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance OI hist returned {Code} for {Symbol}", resp.StatusCode, binanceSymbol);
                return 0;
            }
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var points = new List<OpenInterestPoint>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("sumOpenInterestValue", out var vEl)) continue;
                if (!decimal.TryParse(vEl.GetString(), out var val)) continue;
                if (!el.TryGetProperty("timestamp", out var tEl)) continue;
                long ts = tEl.GetInt64();
                points.Add(new OpenInterestPoint
                {
                    Symbol = normalizedSymbol,
                    SampleTime = DateTimeOffset.FromUnixTimeMilliseconds(ts).UtcDateTime,
                    OiValue = val,
                });
            }
            _db.SaveOpenInterestHist(points);
            _logger.LogInformation("OI hist deep {Symbol}: saved {Count} points (30d API)", binanceSymbol, points.Count);
            return points.Count;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OI hist deep fetch failed: {Symbol}", binanceSymbol);
            return 0;
        }
    }

    // ── Deribit DVOL 隱含波動指數(VRP / 波動 carry)──────────────────

    /// <summary>
    /// Deribit DVOL(BTC/ETH 隱含波動指數,類 VIX)深度回補。
    /// public/get_volatility_index_data:resolution=1D、回 result.data = [[ts_ms,open,high,low,close],…] 日 OHLC(純數字),
    /// 取 close 當當日隱含波動值(年化 %)。免金鑰。固定 700 日窗往回分頁(Deribit 單次上限 ~744 點),
    /// 抓到 targetDays 或資料起點(空窗 = 早於 DVOL 上線、停)。SaveDvolValues 走 (symbol, sample_time) PK upsert、重疊不重複。
    /// </summary>
    public async Task<int> FetchDvolDeepAsync(
        string currency, int targetDays, CancellationToken ct = default)
    {
        var cur = currency.ToUpperInvariant();   // "BTC" / "ETH"
        const long dayMs = 86_400_000L;
        const int chunkDays = 700;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long floorMs = nowMs - (long)targetDays * dayMs;
        long windowEnd = nowMs;
        int totalSaved = 0;
        int maxPages = targetDays / chunkDays + 3;

        for (int page = 0; page < maxPages && windowEnd > floorMs; page++)
        {
            if (ct.IsCancellationRequested) break;
            long windowStart = Math.Max(floorMs, windowEnd - (long)chunkDays * dayMs);

            var url = $"https://www.deribit.com/api/v2/public/get_volatility_index_data" +
                      $"?currency={cur}&start_timestamp={windowStart}&end_timestamp={windowEnd}&resolution=1D";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Deribit DVOL returned {Code} for {Cur} page {Page}", resp.StatusCode, cur, page);
                break;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
                break;

            int len = data.GetArrayLength();
            if (len == 0) break;   // 此窗無資料 = 已早於 DVOL 上線、更早只會更空

            var points = new List<DvolPoint>(len);
            foreach (var row in data.EnumerateArray())
            {
                // row = [timestamp_ms, open, high, low, close]
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 5) continue;
                var close = GetDecimal(row[4]);
                if (close <= 0) continue;
                points.Add(new DvolPoint
                {
                    Symbol     = cur,
                    SampleTime = DateTimeOffset.FromUnixTimeMilliseconds(row[0].GetInt64()).UtcDateTime,
                    DvolValue  = close,
                });
            }
            if (points.Count > 0) _db.SaveDvolValues(points);
            totalSaved += points.Count;

            windowEnd = windowStart - 1;   // 下一窗往回
            await Task.Delay(250, ct).ContinueWith(_ => { });
        }

        _logger.LogInformation("Deribit DVOL deep {Cur}: saved {Count} daily points (target {Target}d)",
            cur, totalSaved, targetDays);
        return totalSaved;
    }

    /// <summary>
    /// 當前未平倉量快照（OI history 只有 ~30 天、故只取即時值當 live 訊號、不落歷史表）。
    /// 回 (openInterest 基幣張數, 取得時間)。失敗回 null。
    /// </summary>
    public async Task<(decimal OpenInterest, DateTime Time)?> FetchOpenInterestNowAsync(
        string binanceSymbol, CancellationToken ct = default)
    {
        var url = $"https://fapi.binance.com/fapi/v1/openInterest?symbol={binanceSymbol}";
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("Binance OI returned {Code} for {Symbol}", resp.StatusCode, binanceSymbol);
            return null;
        }
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("openInterest", out var oiEl)) return null;
        if (!decimal.TryParse(oiEl.GetString(), out var oi)) return null;
        var timeMs = root.TryGetProperty("time", out var tEl) ? tEl.GetInt64()
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return (oi, DateTimeOffset.FromUnixTimeMilliseconds(timeMs).UtcDateTime);
    }

    private static long IntervalToMs(string interval) => interval switch
    {
        "1m"  => 60_000L,
        "5m"  => 300_000L,
        "15m" => 900_000L,
        "30m" => 1_800_000L,
        "1h"  => 3_600_000L,
        "2h"  => 7_200_000L,
        "4h"  => 14_400_000L,
        "6h"  => 21_600_000L,
        "12h" => 43_200_000L,
        "1d"  => 86_400_000L,
        "3d"  => 259_200_000L,
        "1w"  => 604_800_000L,
        _ => 0L,
    };

    /// <summary>
    /// CoinGecko id → Binance symbol 對應。
    /// </summary>
    public static string CoinGeckoToBinance(string coinGeckoId) => coinGeckoId.ToLower() switch
    {
        "bitcoin"  => "BTCUSDT",
        "ethereum" => "ETHUSDT",
        "solana"   => "SOLUSDT",
        "dogecoin" => "DOGEUSDT",
        "bnb"      => "BNBUSDT",
        "xrp"      => "XRPUSDT",
        "cardano"  => "ADAUSDT",
        "avalanche-2" => "AVAXUSDT",
        _ => $"{coinGeckoId.ToUpper()}USDT",
    };

    private static decimal GetDecimal(JsonElement el) =>
        el.ValueKind == JsonValueKind.Number ? el.GetDecimal() :
        decimal.TryParse(el.GetString(), out var d) ? d : 0;
}
