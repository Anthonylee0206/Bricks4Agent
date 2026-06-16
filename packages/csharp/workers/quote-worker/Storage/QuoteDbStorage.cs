using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using QuoteWorker.Models;

namespace QuoteWorker.Storage;

/// <summary>
/// SQLite 持久化層 — 儲存即時報價快照與 OHLCV K 線。
/// DB 檔案預設放在 WorkDir/quote.db。
/// </summary>
public class QuoteDbStorage : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly ILogger<QuoteDbStorage> _logger;

    public QuoteDbStorage(string dbPath, ILogger<QuoteDbStorage> logger)
    {
        _logger = logger;
        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        InitSchema();
        _logger.LogInformation("QuoteDbStorage opened: {Path}", dbPath);
    }

    private void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS quote_snapshot (
                symbol        TEXT    NOT NULL,
                type          TEXT    NOT NULL,  -- 'stock' | 'crypto'
                price         REAL    NOT NULL,
                change        REAL    NOT NULL DEFAULT 0,
                change_pct    REAL    NOT NULL DEFAULT 0,
                market_cap    REAL    NOT NULL DEFAULT 0,
                volume_24h    REAL    NOT NULL DEFAULT 0,
                currency      TEXT    NOT NULL DEFAULT 'USD',
                fetched_at    TEXT    NOT NULL,
                PRIMARY KEY (symbol, fetched_at)
            );

            CREATE INDEX IF NOT EXISTS idx_snapshot_symbol_time
                ON quote_snapshot(symbol, fetched_at);

            CREATE TABLE IF NOT EXISTS ohlcv (
                symbol     TEXT NOT NULL,
                type       TEXT NOT NULL,
                interval   TEXT NOT NULL,  -- '1m','5m','15m','1h','4h','1d','1w'
                open_time  TEXT NOT NULL,
                close_time TEXT NOT NULL,
                open       REAL NOT NULL,
                high       REAL NOT NULL,
                low        REAL NOT NULL,
                close      REAL NOT NULL,
                volume     REAL NOT NULL DEFAULT 0,
                PRIMARY KEY (symbol, interval, open_time)
            );

            CREATE INDEX IF NOT EXISTS idx_ohlcv_lookup
                ON ohlcv(symbol, interval, open_time);

            CREATE TABLE IF NOT EXISTS funding_rate (
                symbol       TEXT NOT NULL,
                funding_time TEXT NOT NULL,  -- ISO,Binance 多為 8h 一次
                funding_rate REAL NOT NULL,
                PRIMARY KEY (symbol, funding_time)
            );

            CREATE INDEX IF NOT EXISTS idx_funding_lookup
                ON funding_rate(symbol, funding_time);

            -- 2026-05-28 Q2 retail L/S contrarian alpha:Binance global account L/S ratio(per-symbol、5min~1d 粒度)
            -- 跟 funding_rate 平行儲存,QuoteOhlcvHandler 同樣對齊到 bars 後 emit retail_long_short_ratio。
            CREATE TABLE IF NOT EXISTS retail_ls_ratio (
                symbol      TEXT NOT NULL,
                sample_time TEXT NOT NULL,    -- ISO,通常 5min 粒度
                ls_ratio    REAL NOT NULL,    -- count_long_short_ratio: long_account/short_account
                PRIMARY KEY (symbol, sample_time)
            );

            CREATE INDEX IF NOT EXISTS idx_retail_ls_lookup
                ON retail_ls_ratio(symbol, sample_time);

            -- 2026-05-29 Q2 oi_contrarian alpha:perp 未平倉量(USDT 名目值)歷史,跟 retail_ls 平行
            -- QuoteOhlcvHandler AlignOi 對齊後 emit open_interest;oi_momentum_ls 算 %change pctile 用
            CREATE TABLE IF NOT EXISTS open_interest_hist (
                symbol      TEXT NOT NULL,
                sample_time TEXT NOT NULL,
                oi_value    REAL NOT NULL,   -- sum_open_interest_value (USDT 名目)
                PRIMARY KEY (symbol, sample_time)
            );

            CREATE INDEX IF NOT EXISTS idx_oi_hist_lookup
                ON open_interest_hist(symbol, sample_time);

            -- 2026-06-16 VRP / 波動 carry shadow:Deribit DVOL 隱含波動指數(BTC/ETH、年化 vol points)
            -- 跟 retail_ls / open_interest 平行,QuoteOhlcvHandler AlignDvol 對齊後 emit dvol。
            -- 隱含波動來源(對照 BTC OHLCV 算的實現波動 → 變異數風險溢酬)。見 docs/designs/vrp-shadow-deploy-sketch.md。
            CREATE TABLE IF NOT EXISTS deribit_dvol (
                symbol      TEXT NOT NULL,    -- Deribit currency:'BTC' / 'ETH'
                sample_time TEXT NOT NULL,
                dvol_value  REAL NOT NULL,    -- 隱含波動指數(年化 %,例 55.0)
                PRIMARY KEY (symbol, sample_time)
            );

            CREATE INDEX IF NOT EXISTS idx_deribit_dvol_lookup
                ON deribit_dvol(symbol, sample_time);
            """;
        cmd.ExecuteNonQuery();
    }

    // ── 即時報價快照 ─────────────────────────────────────────────────

    public void SaveSnapshots(IEnumerable<QuoteResult> quotes)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO quote_snapshot
                (symbol, type, price, change, change_pct, market_cap, volume_24h, currency, fetched_at)
            VALUES
                ($symbol, $type, $price, $change, $changePct, $marketCap, $volume, $currency, $fetchedAt)
            """;

        var pSymbol    = cmd.Parameters.Add("$symbol",    SqliteType.Text);
        var pType      = cmd.Parameters.Add("$type",      SqliteType.Text);
        var pPrice     = cmd.Parameters.Add("$price",     SqliteType.Real);
        var pChange    = cmd.Parameters.Add("$change",    SqliteType.Real);
        var pChangePct = cmd.Parameters.Add("$changePct", SqliteType.Real);
        var pMarketCap = cmd.Parameters.Add("$marketCap", SqliteType.Real);
        var pVolume    = cmd.Parameters.Add("$volume",    SqliteType.Real);
        var pCurrency  = cmd.Parameters.Add("$currency",  SqliteType.Text);
        var pFetchedAt = cmd.Parameters.Add("$fetchedAt", SqliteType.Text);

        int count = 0;
        foreach (var q in quotes)
        {
            pSymbol.Value    = q.Symbol;
            pType.Value      = q.Type;
            pPrice.Value     = (double)q.Price;
            pChange.Value    = (double)q.Change;
            pChangePct.Value = (double)q.ChangePercent;
            pMarketCap.Value = (double)q.MarketCap;
            pVolume.Value    = (double)q.Volume24h;
            pCurrency.Value  = q.Currency;
            pFetchedAt.Value = q.FetchedAt.ToString("o");
            cmd.ExecuteNonQuery();
            count++;
        }

        tx.Commit();
        _logger.LogDebug("Saved {Count} quote snapshots", count);
    }

    public List<QuoteResult> GetSnapshots(string symbol, int limit = 100)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT symbol, type, price, change, change_pct, market_cap, volume_24h, currency, fetched_at
            FROM quote_snapshot
            WHERE symbol = $symbol
            ORDER BY fetched_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<QuoteResult>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new QuoteResult
            {
                Symbol        = reader.GetString(0),
                Type          = reader.GetString(1),
                Price         = (decimal)reader.GetDouble(2),
                Change        = (decimal)reader.GetDouble(3),
                ChangePercent = (decimal)reader.GetDouble(4),
                MarketCap     = (decimal)reader.GetDouble(5),
                Volume24h     = (decimal)reader.GetDouble(6),
                Currency      = reader.GetString(7),
                FetchedAt     = DateTime.Parse(reader.GetString(8)),
            });
        }
        return list;
    }

    // ── OHLCV K 線 ──────────────────────────────────────────────────

    public void SaveBars(IEnumerable<OhlcvBar> bars)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO ohlcv
                (symbol, type, interval, open_time, close_time, open, high, low, close, volume)
            VALUES
                ($symbol, $type, $interval, $openTime, $closeTime, $open, $high, $low, $close, $volume)
            """;

        var pSymbol    = cmd.Parameters.Add("$symbol",    SqliteType.Text);
        var pType      = cmd.Parameters.Add("$type",      SqliteType.Text);
        var pInterval  = cmd.Parameters.Add("$interval",  SqliteType.Text);
        var pOpenTime  = cmd.Parameters.Add("$openTime",  SqliteType.Text);
        var pCloseTime = cmd.Parameters.Add("$closeTime", SqliteType.Text);
        var pOpen      = cmd.Parameters.Add("$open",      SqliteType.Real);
        var pHigh      = cmd.Parameters.Add("$high",      SqliteType.Real);
        var pLow       = cmd.Parameters.Add("$low",       SqliteType.Real);
        var pClose     = cmd.Parameters.Add("$close",     SqliteType.Real);
        var pVolume    = cmd.Parameters.Add("$volume",    SqliteType.Real);

        int count = 0;
        foreach (var b in bars)
        {
            pSymbol.Value    = b.Symbol;
            pType.Value      = b.Type;
            pInterval.Value  = b.Interval;
            pOpenTime.Value  = b.OpenTime.ToString("o");
            pCloseTime.Value = b.CloseTime.ToString("o");
            pOpen.Value      = (double)b.Open;
            pHigh.Value      = (double)b.High;
            pLow.Value       = (double)b.Low;
            pClose.Value     = (double)b.Close;
            pVolume.Value    = (double)b.Volume;
            cmd.ExecuteNonQuery();
            count++;
        }

        tx.Commit();
        _logger.LogDebug("Saved {Count} OHLCV bars", count);
    }

    public List<OhlcvBar> GetBars(string symbol, string interval = "1d", int limit = 365)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT symbol, type, interval, open_time, close_time, open, high, low, close, volume
            FROM ohlcv
            WHERE symbol = $symbol AND interval = $interval
            ORDER BY open_time DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$interval", interval);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<OhlcvBar>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new OhlcvBar
            {
                Symbol    = reader.GetString(0),
                Type      = reader.GetString(1),
                Interval  = reader.GetString(2),
                OpenTime  = DateTime.Parse(reader.GetString(3)),
                CloseTime = DateTime.Parse(reader.GetString(4)),
                Open      = (decimal)reader.GetDouble(5),
                High      = (decimal)reader.GetDouble(6),
                Low       = (decimal)reader.GetDouble(7),
                Close     = (decimal)reader.GetDouble(8),
                Volume    = (decimal)reader.GetDouble(9),
            });
        }

        list.Reverse();
        return list;
    }

    /// <summary>某 symbol+interval 目前有幾根 bar（深度回補的 skip-if-already-deep 判斷用）。</summary>
    public int CountBars(string symbol, string interval)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ohlcv WHERE symbol = $symbol AND interval = $interval";
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$interval", interval);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    /// <summary>取得某 symbol+interval 最新一根 bar 的時間，用於增量抓取。</summary>
    public DateTime? GetLatestBarTime(string symbol, string interval)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT MAX(open_time) FROM ohlcv
            WHERE symbol = $symbol AND interval = $interval
            """;
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$interval", interval);

        var result = cmd.ExecuteScalar();
        if (result is string s && DateTime.TryParse(s, out var dt))
            return dt;
        return null;
    }

    // ── 永續資金費率 ────────────────────────────────────────────────

    public void SaveFundingRates(IEnumerable<FundingRatePoint> points)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO funding_rate (symbol, funding_time, funding_rate)
            VALUES ($symbol, $fundingTime, $fundingRate)
            """;
        var pSymbol      = cmd.Parameters.Add("$symbol",      SqliteType.Text);
        var pFundingTime = cmd.Parameters.Add("$fundingTime", SqliteType.Text);
        var pFundingRate = cmd.Parameters.Add("$fundingRate", SqliteType.Real);

        int count = 0;
        foreach (var p in points)
        {
            pSymbol.Value      = p.Symbol;
            pFundingTime.Value = p.FundingTime.ToString("o");
            pFundingRate.Value = (double)p.FundingRate;
            cmd.ExecuteNonQuery();
            count++;
        }
        tx.Commit();
        _logger.LogDebug("Saved {Count} funding rate points", count);
    }

    public List<FundingRatePoint> GetFundingRates(string symbol, int limit = 1000)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT symbol, funding_time, funding_rate
            FROM funding_rate
            WHERE symbol = $symbol
            ORDER BY funding_time DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<FundingRatePoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new FundingRatePoint
            {
                Symbol      = reader.GetString(0),
                FundingTime = DateTime.Parse(reader.GetString(1)),
                FundingRate = (decimal)reader.GetDouble(2),
            });
        }
        list.Reverse();   // 回傳由舊到新
        return list;
    }

    public int CountFundingRates(string symbol)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM funding_rate WHERE symbol = $symbol";
        cmd.Parameters.AddWithValue("$symbol", symbol);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    // ── 散戶多空比(Q2 retail_ls_contrarian)─────────────────────────────

    public void SaveRetailLsRatios(IEnumerable<RetailLsRatioPoint> points)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO retail_ls_ratio (symbol, sample_time, ls_ratio)
            VALUES ($symbol, $sampleTime, $lsRatio)
            """;
        var pSymbol = cmd.Parameters.Add("$symbol", SqliteType.Text);
        var pTime = cmd.Parameters.Add("$sampleTime", SqliteType.Text);
        var pRatio = cmd.Parameters.Add("$lsRatio", SqliteType.Real);

        int count = 0;
        foreach (var p in points)
        {
            pSymbol.Value = p.Symbol;
            pTime.Value = p.SampleTime.ToString("o");
            pRatio.Value = (double)p.LsRatio;
            cmd.ExecuteNonQuery();
            count++;
        }
        tx.Commit();
        _logger.LogDebug("Saved {Count} retail L/S ratio points", count);
    }

    public List<RetailLsRatioPoint> GetRetailLsRatios(string symbol, int limit = 1000)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT symbol, sample_time, ls_ratio
            FROM retail_ls_ratio
            WHERE symbol = $symbol
            ORDER BY sample_time DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<RetailLsRatioPoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new RetailLsRatioPoint
            {
                Symbol = reader.GetString(0),
                SampleTime = DateTime.Parse(reader.GetString(1)),
                LsRatio = (decimal)reader.GetDouble(2),
            });
        }
        list.Reverse();
        return list;
    }

    public int CountRetailLsRatios(string symbol)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM retail_ls_ratio WHERE symbol = $symbol";
        cmd.Parameters.AddWithValue("$symbol", symbol);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    // ── 未平倉量歷史(Q2 oi_contrarian)──────────────────────────────

    public void SaveOpenInterestHist(IEnumerable<OpenInterestPoint> points)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO open_interest_hist (symbol, sample_time, oi_value)
            VALUES ($symbol, $sampleTime, $oiValue)
            """;
        var pSymbol = cmd.Parameters.Add("$symbol", SqliteType.Text);
        var pTime = cmd.Parameters.Add("$sampleTime", SqliteType.Text);
        var pVal = cmd.Parameters.Add("$oiValue", SqliteType.Real);

        int count = 0;
        foreach (var p in points)
        {
            pSymbol.Value = p.Symbol;
            pTime.Value = p.SampleTime.ToString("o");
            pVal.Value = (double)p.OiValue;
            cmd.ExecuteNonQuery();
            count++;
        }
        tx.Commit();
        _logger.LogDebug("Saved {Count} open interest points", count);
    }

    public List<OpenInterestPoint> GetOpenInterestHist(string symbol, int limit = 1000)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT symbol, sample_time, oi_value
            FROM open_interest_hist
            WHERE symbol = $symbol
            ORDER BY sample_time DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<OpenInterestPoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new OpenInterestPoint
            {
                Symbol = reader.GetString(0),
                SampleTime = DateTime.Parse(reader.GetString(1)),
                OiValue = (decimal)reader.GetDouble(2),
            });
        }
        list.Reverse();
        return list;
    }

    public int CountOpenInterestHist(string symbol)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM open_interest_hist WHERE symbol = $symbol";
        cmd.Parameters.AddWithValue("$symbol", symbol);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    // ── Deribit DVOL 隱含波動指數(VRP / 波動 carry)──────────────────

    public void SaveDvolValues(IEnumerable<DvolPoint> points)
    {
        using var tx = _conn.BeginTransaction();
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO deribit_dvol (symbol, sample_time, dvol_value)
            VALUES ($symbol, $sampleTime, $dvolValue)
            """;
        var pSymbol = cmd.Parameters.Add("$symbol", SqliteType.Text);
        var pTime = cmd.Parameters.Add("$sampleTime", SqliteType.Text);
        var pVal = cmd.Parameters.Add("$dvolValue", SqliteType.Real);

        int count = 0;
        foreach (var p in points)
        {
            pSymbol.Value = p.Symbol;
            pTime.Value = p.SampleTime.ToString("o");
            pVal.Value = (double)p.DvolValue;
            cmd.ExecuteNonQuery();
            count++;
        }
        tx.Commit();
        _logger.LogDebug("Saved {Count} DVOL points", count);
    }

    public List<DvolPoint> GetDvolValues(string symbol, int limit = 1000)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT symbol, sample_time, dvol_value
            FROM deribit_dvol
            WHERE symbol = $symbol
            ORDER BY sample_time DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$symbol", symbol);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<DvolPoint>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new DvolPoint
            {
                Symbol = reader.GetString(0),
                SampleTime = DateTime.Parse(reader.GetString(1)),
                DvolValue = (decimal)reader.GetDouble(2),
            });
        }
        list.Reverse();   // 回傳由舊到新
        return list;
    }

    public int CountDvolValues(string symbol)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM deribit_dvol WHERE symbol = $symbol";
        cmd.Parameters.AddWithValue("$symbol", symbol);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public void Dispose()
    {
        _conn.Close();
        _conn.Dispose();
    }
}
