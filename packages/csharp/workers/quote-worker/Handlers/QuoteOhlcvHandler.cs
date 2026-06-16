using System.Text.Json;
using WorkerSdk;
using QuoteWorker.History;
using QuoteWorker.Models;
using QuoteWorker.Storage;

namespace QuoteWorker.Handlers;

/// <summary>
/// quote.ohlcv — 查詢或抓取 OHLCV 歷史 K 線。
///
/// Routes:
///   get_bars     — 查詢 DB 中的 K 線（參數：symbol, interval, limit）
///   fetch_stock  — 從 Yahoo Finance 抓取美股歷史（參數：symbol, range, interval）
///   fetch_crypto — 從 Binance 抓取加密貨幣歷史（參數：symbol, interval, limit）
/// </summary>
public class QuoteOhlcvHandler : ICapabilityHandler
{
    private readonly QuoteDbStorage _db;
    private readonly HistoricalDataFetcher _fetcher;
    public string CapabilityId => "quote.ohlcv";

    public QuoteOhlcvHandler(QuoteDbStorage db, HistoricalDataFetcher fetcher)
    {
        _db      = db;
        _fetcher = fetcher;
    }

    /// <summary>
    /// 把 BingX / Binance 等交易所慣用的 quote pair 格式（"BTC-USDT" / "BTCUSDT"）
    /// 轉成 DB 內存的基底符號（"BTC"）。FetchCryptoHistoryAsync 寫入時就 strip 掉 USDT、
    /// 所以這裡只是把外部呼叫的多種寫法統一回去。對美股或非標準 pair 直接 pass-through。
    /// </summary>
    internal static string NormalizeCryptoSymbol(string symbol)
    {
        if (string.IsNullOrEmpty(symbol)) return symbol;
        // BingX 風格："BTC-USDT" → "BTC"
        if (symbol.Contains('-'))
        {
            var parts = symbol.Split('-');
            if (parts.Length == 2 && IsCommonQuote(parts[1]))
                return parts[0];
        }
        // Binance 風格（無分隔）："BTCUSDT" → "BTC"。只 strip 已知 quote、避免誤殺 stocks。
        foreach (var quote in CommonQuotes)
        {
            if (symbol.Length > quote.Length && symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
                return symbol[..^quote.Length];
        }
        return symbol;
    }

    private static readonly string[] CommonQuotes = { "USDT", "USDC", "BUSD", "USD" };
    private static bool IsCommonQuote(string s) => CommonQuotes.Any(q => string.Equals(q, s, StringComparison.OrdinalIgnoreCase));

    public async Task<(bool Success, string? ResultPayload, string? Error)> ExecuteAsync(
        string requestId, string route, string payload, string scope, CancellationToken ct)
    {
        var opts = string.IsNullOrWhiteSpace(payload)
            ? new JsonElement()
            : JsonDocument.Parse(payload).RootElement;

        return route switch
        {
            "get_bars"          => GetBars(opts),
            "fetch_stock"       => await FetchStock(opts, ct),
            "fetch_crypto"      => await FetchCrypto(opts, ct),
            "fetch_crypto_deep" => await FetchCryptoDeep(opts, ct),
            "get_funding"       => GetFunding(opts),
            "fetch_funding_deep" => await FetchFundingDeep(opts, ct),
            "get_oi_now"        => await GetOpenInterestNow(opts, ct),
            "get_bars_funding"  => GetBarsFunding(opts),
            "get_dvol"          => GetDvol(opts),
            "fetch_dvol_deep"   => await FetchDvolDeep(opts, ct),
            _ => (false, null, $"Unknown route: {route}")
        };
    }

    private (bool, string?, string?) GetBars(JsonElement opts)
    {
        var symbol   = opts.TryGetProperty("symbol",   out var s) ? s.GetString() ?? "" : "";
        var interval = opts.TryGetProperty("interval", out var i) ? i.GetString() ?? "1d" : "1d";
        var limit    = opts.TryGetProperty("limit",    out var l) ? l.GetInt32() : 365;

        if (string.IsNullOrEmpty(symbol))
            return (false, null, "Missing required parameter: symbol");

        var bars = _db.GetBars(NormalizeCryptoSymbol(symbol), interval, limit);
        var json = JsonSerializer.Serialize(new
        {
            symbol,
            interval,
            count = bars.Count,
            bars = bars.Select(b => new
            {
                open_time  = b.OpenTime,
                close_time = b.CloseTime,
                open       = b.Open,
                high       = b.High,
                low        = b.Low,
                close      = b.Close,
                volume     = b.Volume,
            })
        });
        return (true, json, null);
    }

    /// <summary>get_bars_funding — OHLCV + 資金費率 as-of join,每根 bar 取 ≤ openTime 的最近 funding
    /// (向前填充)。給 strategy-worker 的 character_ensemble funding 維度直接餵。OI history 不可得,
    /// 故此路由只對齊 funding;OI 即時值走 get_oi_now。</summary>
    private (bool, string?, string?) GetBarsFunding(JsonElement opts)
    {
        var symbol   = opts.TryGetProperty("symbol",   out var s) ? s.GetString() ?? "" : "";
        var interval = opts.TryGetProperty("interval", out var i) ? i.GetString() ?? "1d" : "1d";
        var limit    = opts.TryGetProperty("limit",    out var l) ? l.GetInt32() : 365;
        if (string.IsNullOrEmpty(symbol))
            return (false, null, "Missing required parameter: symbol");

        var sym = NormalizeCryptoSymbol(symbol);
        var bars = _db.GetBars(sym, interval, limit);
        var fundings = _db.GetFundingRates(sym, 2000);
        var retailLs = _db.GetRetailLsRatios(sym, 5000);   // Q2 retail_ls_contrarian alpha
        var oiHist = _db.GetOpenInterestHist(sym, 5000);   // Q2 oi_contrarian alpha
        var dvol = _db.GetDvolValues(sym, 5000);           // VRP/波動 carry(BTC/ETH 才有、其餘空→null)
        var merged = AlignFunding(bars, fundings);
        var lsMerged = AlignRetailLs(bars, retailLs);
        var oiMerged = AlignOi(bars, oiHist);
        var dvolMerged = AlignDvol(bars, dvol);

        var json = JsonSerializer.Serialize(new
        {
            symbol,
            interval,
            count = merged.Count,
            funding_points = fundings.Count,
            retail_ls_points = retailLs.Count,
            oi_points = oiHist.Count,
            dvol_points = dvol.Count,
            bars = merged.Select((m, idx) => new
            {
                open_time  = m.Bar.OpenTime,
                close_time = m.Bar.CloseTime,
                open       = m.Bar.Open,
                high       = m.Bar.High,
                low        = m.Bar.Low,
                close      = m.Bar.Close,
                volume     = m.Bar.Volume,
                funding_rate = m.FundingRate,
                retail_long_short_ratio = idx < lsMerged.Count ? lsMerged[idx].LsRatio : null,
                open_interest = idx < oiMerged.Count ? oiMerged[idx].OiValue : null,
                dvol = idx < dvolMerged.Count ? dvolMerged[idx].DvolValue : null,
            })
        });
        return (true, json, null);
    }

    /// <summary>把 8h 一次的 funding 序列 as-of join 到每根 bar:取 ≤ bar.OpenTime 的最近一筆 funding
    /// (向前填充)。早於第一筆 funding 的 bar → null(strategy 端 FundingBias 自動降級)。純函式、可測。</summary>
    public static List<(OhlcvBar Bar, decimal? FundingRate)> AlignFunding(
        List<OhlcvBar> bars, List<FundingRatePoint> fundings)
    {
        var sortedBars = bars.OrderBy(b => b.OpenTime).ToList();
        var sortedFund = fundings.OrderBy(f => f.FundingTime).ToList();
        var outl = new List<(OhlcvBar, decimal?)>(sortedBars.Count);
        int fi = 0;
        decimal? last = null;
        foreach (var b in sortedBars)
        {
            while (fi < sortedFund.Count && sortedFund[fi].FundingTime <= b.OpenTime)
            {
                last = sortedFund[fi].FundingRate;
                fi++;
            }
            outl.Add((b, last));
        }
        return outl;
    }

    /// <summary>同 AlignFunding pattern,把 5min~1d retail_ls 序列 as-of join 到 bars(向前填充)。
    /// 早於第一筆 sample 的 bar → null,strategy 端自動降級 hold。Q2 retail_ls_contrarian 用。</summary>
    public static List<(OhlcvBar Bar, decimal? LsRatio)> AlignRetailLs(
        List<OhlcvBar> bars, List<RetailLsRatioPoint> ls)
    {
        var sortedBars = bars.OrderBy(b => b.OpenTime).ToList();
        var sortedLs = ls.OrderBy(p => p.SampleTime).ToList();
        var outl = new List<(OhlcvBar, decimal?)>(sortedBars.Count);
        int li = 0;
        decimal? last = null;
        foreach (var b in sortedBars)
        {
            while (li < sortedLs.Count && sortedLs[li].SampleTime <= b.OpenTime)
            {
                last = sortedLs[li].LsRatio;
                li++;
            }
            outl.Add((b, last));
        }
        return outl;
    }

    /// <summary>同 AlignFunding pattern,把 OI 歷史 as-of join 到 bars(向前填充)。Q2 oi_contrarian 用。</summary>
    public static List<(OhlcvBar Bar, decimal? OiValue)> AlignOi(
        List<OhlcvBar> bars, List<OpenInterestPoint> oi)
    {
        var sortedBars = bars.OrderBy(b => b.OpenTime).ToList();
        var sortedOi = oi.OrderBy(p => p.SampleTime).ToList();
        var outl = new List<(OhlcvBar, decimal?)>(sortedBars.Count);
        int oiIdx = 0;
        decimal? last = null;
        foreach (var b in sortedBars)
        {
            while (oiIdx < sortedOi.Count && sortedOi[oiIdx].SampleTime <= b.OpenTime)
            {
                last = sortedOi[oiIdx].OiValue;
                oiIdx++;
            }
            outl.Add((b, last));
        }
        return outl;
    }

    /// <summary>同 AlignFunding pattern,把 Deribit DVOL 隱含波動序列 as-of join 到 bars(向前填充)。
    /// 早於第一筆的 bar → null。VRP / 波動 carry 用(隱含波動,對照 OHLCV 算的實現波動)。純函式、可測。</summary>
    public static List<(OhlcvBar Bar, decimal? DvolValue)> AlignDvol(
        List<OhlcvBar> bars, List<DvolPoint> dvol)
    {
        var sortedBars = bars.OrderBy(b => b.OpenTime).ToList();
        var sortedDvol = dvol.OrderBy(p => p.SampleTime).ToList();
        var outl = new List<(OhlcvBar, decimal?)>(sortedBars.Count);
        int di = 0;
        decimal? last = null;
        foreach (var b in sortedBars)
        {
            while (di < sortedDvol.Count && sortedDvol[di].SampleTime <= b.OpenTime)
            {
                last = sortedDvol[di].DvolValue;
                di++;
            }
            outl.Add((b, last));
        }
        return outl;
    }

    private async Task<(bool, string?, string?)> FetchStock(JsonElement opts, CancellationToken ct)
    {
        var symbol   = opts.TryGetProperty("symbol",   out var s) ? s.GetString() ?? "" : "";
        var range    = opts.TryGetProperty("range",    out var r) ? r.GetString() ?? "2y" : "2y";
        var interval = opts.TryGetProperty("interval", out var i) ? i.GetString() ?? "1d" : "1d";

        if (string.IsNullOrEmpty(symbol))
            return (false, null, "Missing required parameter: symbol");

        var count = await _fetcher.FetchStockHistoryAsync(symbol, range, interval, ct);
        var json = JsonSerializer.Serialize(new { symbol, range, interval, bars_saved = count });
        return (true, json, null);
    }

    private async Task<(bool, string?, string?)> FetchCrypto(JsonElement opts, CancellationToken ct)
    {
        var symbol   = opts.TryGetProperty("symbol",   out var s) ? s.GetString() ?? "" : "";
        var interval = opts.TryGetProperty("interval", out var i) ? i.GetString() ?? "1d" : "1d";
        var limit    = opts.TryGetProperty("limit",    out var l) ? l.GetInt32() : 365;

        if (string.IsNullOrEmpty(symbol))
            return (false, null, "Missing required parameter: symbol");

        var binanceSymbol = HistoricalDataFetcher.CoinGeckoToBinance(symbol);
        var count = await _fetcher.FetchCryptoHistoryAsync(binanceSymbol, interval, limit, ct);
        var json = JsonSerializer.Serialize(new { symbol, binance_symbol = binanceSymbol, interval, bars_saved = count });
        return (true, json, null);
    }

    /// <summary>
    /// fetch_crypto_deep — 深度回補：分頁抓過去 target_bars 根到現在。
    /// symbol 可直接給 binance 符號（"BTCUSDT"）或 coingecko id（"bitcoin"）。
    /// 參數：symbol, interval（預設 1d）, target_bars（預設 1500）。
    /// </summary>
    private async Task<(bool, string?, string?)> FetchCryptoDeep(JsonElement opts, CancellationToken ct)
    {
        var symbol     = opts.TryGetProperty("symbol",      out var s) ? s.GetString() ?? "" : "";
        var interval   = opts.TryGetProperty("interval",    out var i) ? i.GetString() ?? "1d" : "1d";
        var targetBars = opts.TryGetProperty("target_bars", out var t) ? t.GetInt32() : 1500;

        if (string.IsNullOrEmpty(symbol))
            return (false, null, "Missing required parameter: symbol");

        // 已是 binance 符號（含 USDT 等 quote）就直接用；否則當 coingecko id 轉換
        var binanceSymbol = CommonQuotes.Any(q => symbol.EndsWith(q, StringComparison.OrdinalIgnoreCase))
            ? symbol.ToUpperInvariant()
            : HistoricalDataFetcher.CoinGeckoToBinance(symbol);

        var count = await _fetcher.FetchCryptoDeepAsync(binanceSymbol, interval, targetBars, ct);
        var json = JsonSerializer.Serialize(new
        {
            symbol, binance_symbol = binanceSymbol, interval, target_bars = targetBars, bars_saved = count
        });
        return (true, json, null);
    }

    // ── 永續資金費率 + 未平倉量 ─────────────────────────────────────

    /// <summary>get_funding — 查 DB 的資金費率序列（參數：symbol, limit 預設 1000）。</summary>
    private (bool, string?, string?) GetFunding(JsonElement opts)
    {
        var symbol = opts.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
        var limit  = opts.TryGetProperty("limit",  out var l) ? l.GetInt32() : 1000;
        if (string.IsNullOrEmpty(symbol))
            return (false, null, "Missing required parameter: symbol");

        var points = _db.GetFundingRates(NormalizeCryptoSymbol(symbol), limit);
        var json = JsonSerializer.Serialize(new
        {
            symbol,
            count = points.Count,
            funding = points.Select(p => new { funding_time = p.FundingTime, funding_rate = p.FundingRate })
        });
        return (true, json, null);
    }

    /// <summary>fetch_funding_deep — 深度回補資金費率（參數：symbol, target_points 預設 1000）。</summary>
    private async Task<(bool, string?, string?)> FetchFundingDeep(JsonElement opts, CancellationToken ct)
    {
        var symbol       = opts.TryGetProperty("symbol",        out var s) ? s.GetString() ?? "" : "";
        var targetPoints = opts.TryGetProperty("target_points", out var t) ? t.GetInt32() : 1000;
        if (string.IsNullOrEmpty(symbol))
            return (false, null, "Missing required parameter: symbol");

        var binanceSymbol = ToBinanceSymbol(symbol);
        var count = await _fetcher.FetchFundingRateDeepAsync(binanceSymbol, targetPoints, ct);
        var json = JsonSerializer.Serialize(new
        {
            symbol, binance_symbol = binanceSymbol, target_points = targetPoints, points_saved = count
        });
        return (true, json, null);
    }

    /// <summary>get_oi_now — 當前未平倉量快照（OI history 只 ~30 天、故只給即時值當 live 訊號）。</summary>
    private async Task<(bool, string?, string?)> GetOpenInterestNow(JsonElement opts, CancellationToken ct)
    {
        var symbol = opts.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(symbol))
            return (false, null, "Missing required parameter: symbol");

        var binanceSymbol = ToBinanceSymbol(symbol);
        var oi = await _fetcher.FetchOpenInterestNowAsync(binanceSymbol, ct);
        if (oi == null)
            return (false, null, $"open interest unavailable for {binanceSymbol}");

        var json = JsonSerializer.Serialize(new
        {
            symbol, binance_symbol = binanceSymbol,
            open_interest = oi.Value.OpenInterest, time = oi.Value.Time
        });
        return (true, json, null);
    }

    // ── Deribit DVOL 隱含波動指數(VRP / 波動 carry)──────────────────

    /// <summary>get_dvol — 查 DB 的 Deribit DVOL 隱含波動序列(參數:symbol/currency BTC|ETH, limit 預設 1000)。</summary>
    private (bool, string?, string?) GetDvol(JsonElement opts)
    {
        var symbol = opts.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
        var limit  = opts.TryGetProperty("limit",  out var l) ? l.GetInt32() : 1000;
        if (string.IsNullOrEmpty(symbol))
            return (false, null, "Missing required parameter: symbol");

        var points = _db.GetDvolValues(NormalizeCryptoSymbol(symbol).ToUpperInvariant(), limit);
        var json = JsonSerializer.Serialize(new
        {
            symbol,
            count = points.Count,
            dvol = points.Select(p => new { sample_time = p.SampleTime, dvol = p.DvolValue })
        });
        return (true, json, null);
    }

    /// <summary>fetch_dvol_deep — 深度回補 Deribit DVOL(參數:symbol/currency BTC|ETH, target_days 預設 1500)。</summary>
    private async Task<(bool, string?, string?)> FetchDvolDeep(JsonElement opts, CancellationToken ct)
    {
        var symbol     = opts.TryGetProperty("symbol",      out var s) ? s.GetString() ?? "" : "";
        var targetDays = opts.TryGetProperty("target_days", out var t) ? t.GetInt32() : 1500;
        if (string.IsNullOrEmpty(symbol))
            return (false, null, "Missing required parameter: symbol");

        var cur = NormalizeCryptoSymbol(symbol).ToUpperInvariant();   // "BTC" / "ETH"
        var count = await _fetcher.FetchDvolDeepAsync(cur, targetDays, ct);
        var json = JsonSerializer.Serialize(new { symbol, currency = cur, target_days = targetDays, points_saved = count });
        return (true, json, null);
    }

    /// <summary>外部 symbol（"BTC-USDT" / "bitcoin" / "BTCUSDT"）→ Binance 符號（"BTCUSDT"）。</summary>
    private static string ToBinanceSymbol(string symbol)
    {
        if (symbol.Contains('-'))
        {
            var parts = symbol.Split('-');
            if (parts.Length == 2 && IsCommonQuote(parts[1]))
                return (parts[0] + parts[1]).ToUpperInvariant();
        }
        return CommonQuotes.Any(q => symbol.EndsWith(q, StringComparison.OrdinalIgnoreCase))
            ? symbol.ToUpperInvariant()
            : HistoricalDataFetcher.CoinGeckoToBinance(symbol);
    }
}
