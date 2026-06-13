using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingWorker.Models;

namespace TradingWorker.Exchange;

/// <summary>
/// BingX USDT-M Perpetual Swap V2 API 客戶端。
///
/// 文件：https://bingx-api.github.io/docs/#/swapV2/
///
/// 認證：HMAC-SHA256 簽 `{queryString}&timestamp=...`，header 帶 `X-BX-APIKEY`。
/// signature 加在 query 結尾或 body 結尾（按 method 不同）。
///
/// IsDemo:
///   true  → https://open-api-vst.bingx.com（VST demo、給虛擬 USDT）
///   false → https://open-api.bingx.com（實盤）
///
/// Side / PositionSide 對映：
///   open long  → side=BUY,  positionSide=LONG
///   close long → side=SELL, positionSide=LONG
///   open short → side=SELL, positionSide=SHORT
///   close short → side=BUY, positionSide=SHORT
/// </summary>
public class BingxPerpetualClient : IPerpetualClient
{
    private readonly HttpClient _http;
    private readonly ILogger<BingxPerpetualClient> _logger;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly string _baseUrl;

    public string ExchangeName => "bingx";
    public bool IsDemo { get; }

    public BingxPerpetualClient(
        HttpClient http,
        ILogger<BingxPerpetualClient> logger,
        string apiKey,
        string apiSecret,
        bool isDemo = true)
    {
        _http = http;
        _logger = logger;
        _apiKey = apiKey;
        _apiSecret = apiSecret;
        IsDemo = isDemo;
        _baseUrl = isDemo
            ? "https://open-api-vst.bingx.com"
            : "https://open-api.bingx.com";

        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Add("X-BX-APIKEY", apiKey);
    }

    // ── Account ─────────────────────────────────────────────────────

    public async Task<PerpetualAccount> GetAccountAsync(CancellationToken ct = default)
    {
        // GET /openApi/swap/v2/user/balance
        var json = await SignedGetAsync("/openApi/swap/v2/user/balance", "", ct);
        var doc = JsonDocument.Parse(json).RootElement;
        EnsureOk(doc, "GetAccount");

        // BingX V2 回 { code, msg, data: { balance: { ... } } }
        var data = doc.GetProperty("data");
        var bal = data.TryGetProperty("balance", out var b) ? b : data;

        var balance = ParseDec(bal, "balance");
        var equity = ParseDec(bal, "equity");
        var unrealized = ParseDec(bal, "unrealizedProfit");
        var marginUsed = ParseDec(bal, "usedMargin");
        var available = ParseDec(bal, "availableMargin");
        var userId = bal.TryGetProperty("userId", out var u) ? u.ToString() : "bingx-vst";

        // 數開倉 — 順帶拉一次 positions（不算太貴、bingx 沒收費）
        var positions = await GetPositionsAsync(ct);

        return new PerpetualAccount
        {
            Exchange = ExchangeName,
            AccountId = string.IsNullOrEmpty(userId) ? "bingx-vst" : userId,
            Currency = "USDT",
            Balance = balance,
            Equity = equity > 0 ? equity : balance + unrealized,
            UnrealizedPnl = unrealized,
            MarginUsed = marginUsed,
            AvailableMargin = available > 0 ? available : Math.Max(0m, balance - marginUsed),
            OpenPositionsCount = positions.Count,
            IsDemo = IsDemo,
            UpdatedAt = DateTime.UtcNow,
        };
    }

    // ── Positions ───────────────────────────────────────────────────

    public async Task<List<PerpetualPosition>> GetPositionsAsync(CancellationToken ct = default)
    {
        // GET /openApi/swap/v2/user/positions
        var json = await SignedGetAsync("/openApi/swap/v2/user/positions", "", ct);
        var doc = JsonDocument.Parse(json).RootElement;
        EnsureOk(doc, "GetPositions");

        var list = new List<PerpetualPosition>();
        if (!doc.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var p in data.EnumerateArray())
        {
            var qty = ParseDec(p, "positionAmt");
            if (qty == 0m) continue;  // BingX 即使沒倉也會回零部位、過濾掉

            var symbol = p.GetProperty("symbol").GetString() ?? "";
            var side = (p.TryGetProperty("positionSide", out var ps) ? ps.GetString() ?? "LONG" : "LONG").ToLowerInvariant();
            var entry = ParseDec(p, "avgPrice");
            var mark = ParseDec(p, "markPrice");
            var unrealized = ParseDec(p, "unrealizedProfit");
            var leverage = p.TryGetProperty("leverage", out var lv) && lv.TryGetInt32(out var lvI) ? lvI : 1;
            var marginMode = (p.TryGetProperty("marginMode", out var mm) ? mm.GetString() ?? "isolated" : "isolated").ToLowerInvariant();
            var marginUsed = ParseDec(p, "initialMargin");
            var liqPrice = ParseDec(p, "liquidationPrice");

            var pnlPct = entry > 0m && qty != 0m
                ? (side == "long" ? (mark - entry) / entry * 100m : (entry - mark) / entry * 100m)
                : 0m;

            var liqDist = mark > 0m && liqPrice > 0m
                ? Math.Abs((mark - liqPrice) / mark) * 100m
                : 0m;

            list.Add(new PerpetualPosition
            {
                Symbol = symbol,
                Exchange = ExchangeName,
                Side = side,
                Quantity = Math.Abs(qty),
                AvgEntryPrice = entry,
                MarkPrice = mark,
                UnrealizedPnl = unrealized,
                UnrealizedPnlPercent = Math.Round(pnlPct, 4),
                Leverage = leverage,
                MarginMode = marginMode,
                MarginUsed = marginUsed,
                LiquidationPrice = liqPrice,
                LiquidationDistancePct = Math.Round(liqDist, 4),
                UpdatedAt = DateTime.UtcNow,
            });
        }
        return list;
    }

    // ── Orders ──────────────────────────────────────────────────────

    public async Task<PerpetualOrder> PlaceOrderAsync(PerpetualOrder order, CancellationToken ct = default)
    {
        // POST /openApi/swap/v2/trade/order
        // 必填：symbol, side, positionSide, type, quantity（market）/ price（limit）
        var qs = new Dictionary<string, string>
        {
            ["symbol"]       = order.Symbol,
            ["side"]         = order.Side.ToUpperInvariant(),                    // BUY / SELL
            ["positionSide"] = order.PositionSide.ToUpperInvariant(),            // LONG / SHORT
            ["type"]         = MapOrderType(order.OrderType),
            ["quantity"]     = order.Quantity.ToString(CultureInfo.InvariantCulture),
        };
        // 冪等 key：把 OrderId 當 BingX clientOrderID 送（2026-06-12 live 驗證:BingX 對重複 clientOrderID
        // 回 code=101400）。failover 重送同一 deterministic key → BingX 擋 → 不雙下真錢單。
        // OrderId 一律有值;unique 時不觸發 dedup（零影響）、deterministic 重送時才擋。
        if (!string.IsNullOrWhiteSpace(order.OrderId))
            qs["clientOrderID"] = order.OrderId.Length <= 36 ? order.OrderId : order.OrderId.Substring(0, 36);
        if (order.OrderType == "limit" && order.LimitPrice.HasValue)
            qs["price"] = order.LimitPrice.Value.ToString(CultureInfo.InvariantCulture);
        if (order.StopPrice.HasValue)
            qs["stopPrice"] = order.StopPrice.Value.ToString(CultureInfo.InvariantCulture);
        // BingX 在 hedge mode（雙向持倉）不接受 reduceOnly——側向已被 (side, positionSide) 隱含：
        // SELL+LONG = 平多、BUY+SHORT = 平空。傳了會回 code=109400。
        // ReduceOnly 旗標仍保留在 PerpetualOrder 上、給 caller 表達意圖、但不送到 BingX。

        // C3 — Bracket order：開倉時帶 TP/SL JSON、BingX 自動 attach 到 position（server-side）。
        // broker crash 不會留裸位、SL 在 exchange 端保護到 broker 重啟。
        // 兩條都只在「真開倉」（非平倉）才送、否則 BingX 會拒絕。
        if (!order.ReduceOnly)
        {
            if (order.TakeProfitPrice.HasValue && order.TakeProfitPrice.Value > 0m)
            {
                var tpJson = JsonSerializer.Serialize(new
                {
                    type = "TAKE_PROFIT_MARKET",
                    stopPrice = order.TakeProfitPrice.Value,
                    workingType = "MARK_PRICE",
                });
                qs["takeProfit"] = tpJson;
            }
            // finding I:SL **不再內嵌**進這張原子單。原本內嵌 stopLoss 時,若市價腿成交但內嵌 SL 被 BingX
            // 拒(壞 tick / 距離 / 精度),EnsureOk 會拋例外 → 整單當失敗 → broker 不追蹤 → 孤兒裸倉(有倉無 SL)。
            // 改成:市價腿成交後、另發一張「獨立」reduce-only STOP_MARKET(見下方 return 前),
            // SL 失敗也不會 unwind 已成交的進場(只標 filled_no_sl 讓 broker soft-SL 接管)。
            // TP 失敗非安全風險(沒成交頂多少賺)、保留內嵌。
        }

        var json = await SignedPostFormAsync("/openApi/swap/v2/trade/order", qs, ct);
        var doc = JsonDocument.Parse(json).RootElement;
        // 冪等命中:clientOrderID 重複（failover 重送同一單）→ BingX code=101400 'clientOrderID unique
        // check failed' → 視為「已下過」、非錯誤、不重複下單（這是 failover 不雙下單的關鍵）。
        if (doc.TryGetProperty("code", out var dupCode) && dupCode.ValueKind == JsonValueKind.Number
            && dupCode.GetInt32() == 101400)
        {
            order.Status = "idempotent_duplicate";
            order.Error = null;
            order.UpdatedAt = DateTime.UtcNow;
            return order;
        }
        EnsureOk(doc, "PlaceOrder");

        // BingX 回 { data: { order: { orderId, status, ... } } }
        var data = doc.GetProperty("data");
        var ord = data.TryGetProperty("order", out var o) ? o : data;
        order.ExternalId = ord.TryGetProperty("orderId", out var oid) ? oid.ToString() : null;
        order.Status = MapOrderStatus(ord.TryGetProperty("status", out var st) ? st.GetString() ?? "submitted" : "submitted");
        order.UpdatedAt = DateTime.UtcNow;
        if (ord.TryGetProperty("avgPrice", out var ap)) order.FilledPrice = ParseDecValue(ap);
        if (ord.TryGetProperty("executedQty", out var eq)) order.FilledQty = ParseDecValue(eq);

        // finding I:進場是 live(成交/部分/已提交)→ 把 SL 當「獨立」reduce-only STOP_MARKET 補掛。
        // 整段包 try/catch:SL 失敗只標 filled_no_sl + Error,**絕不**讓已成交的進場被丟掉(否則 = 孤兒裸倉)。
        // BuildStopLossOrder 設 ReduceOnly=true → 遞迴呼叫時上面的 !ReduceOnly 區塊不掛 TP/SL,遞迴有界。
        if (!order.ReduceOnly && order.StopLossPrice is decimal slPx && slPx > 0m
            && order.Status is "filled" or "partial" or "submitted")
        {
            var slOrder = BuildStopLossOrder(order, slPx);
            try
            {
                var slResult = await PlaceOrderAsync(slOrder, ct);   // 走標準 STOP_MARKET 路徑、沿用 101400 冪等
                // idempotent_duplicate(failover 重送同一 -sl key)= 先前已掛、Error=null → 不算失敗。
                if (slResult.Status == "rejected" || slResult.Error != null)
                {
                    order.Status = "filled_no_sl";
                    order.Error = $"entry filled but SL rejected: {slResult.Error ?? slResult.Status}";
                    _logger.LogError("BingX entry filled but standalone SL rejected for {Sym} {Side}/{Pos}: {Err}",
                        order.Symbol, order.Side, order.PositionSide, order.Error);
                }
            }
            catch (Exception ex)
            {
                order.Status = "filled_no_sl";
                order.Error = $"entry filled but SL placement threw: {ex.Message}";
                _logger.LogError(ex, "BingX entry filled but SL placement threw for {Sym} {Side}/{Pos}",
                    order.Symbol, order.Side, order.PositionSide);
            }
        }
        return order;
    }

    /// <summary>
    /// finding I — 純函式:從一張開倉單推出對應的 reduce-only STOP_MARKET 平倉保護單。
    /// close side 為開倉 side 的反向(BUY↔SELL)、positionSide 不變、reduceOnly=true、type=stop_market、qty 沿用。
    /// OrderId 加 "-sl" 後綴沿用冪等(failover 重送同一保護單被 BingX 以 101400 擋);base 先截到 33 char 確保 ≤36。
    /// </summary>
    internal static PerpetualOrder BuildStopLossOrder(PerpetualOrder entry, decimal slPrice)
    {
        var closeSide = entry.Side.Equals("buy", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy";
        var slId = string.IsNullOrWhiteSpace(entry.OrderId)
            ? string.Empty
            : (entry.OrderId.Length > 33 ? entry.OrderId.Substring(0, 33) : entry.OrderId) + "-sl";
        return new PerpetualOrder
        {
            OrderId = slId,
            Symbol = entry.Symbol, Exchange = entry.Exchange,
            Side = closeSide, PositionSide = entry.PositionSide,
            OrderType = "stop_market", Quantity = entry.Quantity,
            StopPrice = slPrice, ReduceOnly = true,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
    }

    public async Task<PerpetualOrder> CancelOrderAsync(string symbol, string externalId, CancellationToken ct = default)
    {
        // DELETE /openApi/swap/v2/trade/order
        var qs = $"symbol={symbol}&orderId={externalId}";
        var json = await SignedDeleteAsync("/openApi/swap/v2/trade/order", qs, ct);
        var doc = JsonDocument.Parse(json).RootElement;
        EnsureOk(doc, "CancelOrder");
        return new PerpetualOrder
        {
            ExternalId = externalId, Symbol = symbol, Exchange = ExchangeName,
            Status = "cancelled", UpdatedAt = DateTime.UtcNow,
        };
    }

    public async Task<PerpetualOrder?> GetOrderStatusAsync(string symbol, string externalId, CancellationToken ct = default)
    {
        var json = await SignedGetAsync("/openApi/swap/v2/trade/order", $"symbol={symbol}&orderId={externalId}", ct);
        var doc = JsonDocument.Parse(json).RootElement;
        if (!IsOk(doc)) return null;
        if (!doc.TryGetProperty("data", out var data)) return null;
        var ord = data.TryGetProperty("order", out var o) ? o : data;
        return new PerpetualOrder
        {
            ExternalId = ord.TryGetProperty("orderId", out var oid) ? oid.ToString() : externalId,
            Symbol = symbol,
            Exchange = ExchangeName,
            Side = (ord.TryGetProperty("side", out var sd) ? sd.GetString() ?? "" : "").ToLowerInvariant(),
            PositionSide = (ord.TryGetProperty("positionSide", out var pss) ? pss.GetString() ?? "" : "").ToLowerInvariant(),
            OrderType = (ord.TryGetProperty("type", out var ty) ? ty.GetString() ?? "market" : "market").ToLowerInvariant(),
            Quantity = ParseDec(ord, "origQty"),
            FilledQty = ParseDec(ord, "executedQty"),
            FilledPrice = ord.TryGetProperty("avgPrice", out var ap) ? ParseDecValue(ap) : null,
            Status = MapOrderStatus(ord.TryGetProperty("status", out var st) ? st.GetString() ?? "submitted" : "submitted"),
            UpdatedAt = DateTime.UtcNow,
        };
    }

    public async Task<List<PerpetualOrder>> GetOpenOrdersAsync(string? symbol = null, CancellationToken ct = default)
    {
        var qs = string.IsNullOrEmpty(symbol) ? "" : $"symbol={symbol}";
        var json = await SignedGetAsync("/openApi/swap/v2/trade/openOrders", qs, ct);
        var doc = JsonDocument.Parse(json).RootElement;
        var list = new List<PerpetualOrder>();
        if (!IsOk(doc)) return list;
        if (!doc.TryGetProperty("data", out var data)) return list;
        var arr = data.TryGetProperty("orders", out var ords) ? ords : data;
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var o in arr.EnumerateArray())
        {
            // 之前漏解析 stopPrice / reduceOnly / 真實時間 → 查倉看不到止損價、無法稽核保護。補齊:
            var sp = ParseDec(o, "stopPrice");
            var lp = ParseDec(o, "price");
            bool ro = o.TryGetProperty("reduceOnly", out var rv) &&
                      (rv.ValueKind == JsonValueKind.True ||
                       (rv.ValueKind == JsonValueKind.String && string.Equals(rv.GetString(), "true", StringComparison.OrdinalIgnoreCase)));
            list.Add(new PerpetualOrder
            {
                ExternalId = o.TryGetProperty("orderId", out var oid) ? oid.ToString() : null,
                Symbol = o.TryGetProperty("symbol", out var sym) ? sym.GetString() ?? "" : "",
                Exchange = ExchangeName,
                Side = (o.TryGetProperty("side", out var sd) ? sd.GetString() ?? "" : "").ToLowerInvariant(),
                PositionSide = (o.TryGetProperty("positionSide", out var pss) ? pss.GetString() ?? "" : "").ToLowerInvariant(),
                OrderType = (o.TryGetProperty("type", out var ty) ? ty.GetString() ?? "market" : "market").ToLowerInvariant(),
                Quantity = ParseDec(o, "origQty"),
                FilledQty = ParseDec(o, "executedQty"),
                LimitPrice = lp > 0m ? lp : null,
                StopPrice  = sp > 0m ? sp : null,
                ReduceOnly = ro,
                Status = MapOrderStatus(o.TryGetProperty("status", out var st) ? st.GetString() ?? "submitted" : "submitted"),
                CreatedAt = ParseEpochMs(o, "time"),
                UpdatedAt = ParseEpochMs(o, "updateTime"),
            });
        }
        return list;
    }

    // BingX 時間欄位(time / updateTime)可能是 number 或 string 的 epoch ms;都接、解不出回 now。
    private static DateTime ParseEpochMs(JsonElement o, string key)
    {
        if (!o.TryGetProperty(key, out var v)) return DateTime.UtcNow;
        long ms = v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
                : v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var s) ? s
                : 0;
        return ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow;
    }

    // ── Leverage / Mark price ──────────────────────────────────────

    public async Task<bool> SetLeverageAsync(string symbol, string positionSide, int leverage, CancellationToken ct = default)
    {
        // POST /openApi/swap/v2/trade/leverage
        var qs = $"symbol={symbol}&side={positionSide.ToUpperInvariant()}&leverage={leverage}";
        try
        {
            var json = await SignedPostAsync("/openApi/swap/v2/trade/leverage", qs, ct);
            var doc = JsonDocument.Parse(json).RootElement;
            return IsOk(doc);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BingX SetLeverage failed for {Symbol} {Side} {Lev}", symbol, positionSide, leverage);
            return false;
        }
    }

    public async Task<decimal> GetMarkPriceAsync(string symbol, CancellationToken ct = default)
    {
        // GET /openApi/swap/v2/quote/premiumIndex （mark price + funding）
        // 注意：這個是 public endpoint、不需要簽
        var resp = await _http.GetAsync($"{_baseUrl}/openApi/swap/v2/quote/premiumIndex?symbol={symbol}", ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json).RootElement;
        if (!IsOk(doc) || !doc.TryGetProperty("data", out var data)) return 0m;
        return ParseDec(data, "markPrice");
    }

    public async Task<List<PerpetualIncome>> GetIncomeHistoryAsync(string? symbol, DateTime? sinceUtc, CancellationToken ct = default)
    {
        // GET /openApi/swap/v2/user/income
        // 回 array of { symbol, incomeType, income, asset, info, time, tranId, tradeId }
        // incomeType 包含：REALIZED_PNL / COMMISSION / FUNDING_FEE / TRANSFER 等
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(symbol)) qs.Add($"symbol={Uri.EscapeDataString(symbol)}");
        if (sinceUtc.HasValue)
            qs.Add($"startTime={new DateTimeOffset(sinceUtc.Value, TimeSpan.Zero).ToUnixTimeMilliseconds()}");
        qs.Add("limit=1000");

        var json = await SignedGetAsync("/openApi/swap/v2/user/income", string.Join("&", qs), ct);
        var doc = JsonDocument.Parse(json).RootElement;
        var result = new List<PerpetualIncome>();
        if (!IsOk(doc)) return result;

        // BingX 偶爾把 data 包成 { rows: [...] }、偶爾直接 array、兩種都接
        JsonElement arr;
        if (!doc.TryGetProperty("data", out var data)) return result;
        if (data.ValueKind == JsonValueKind.Array) arr = data;
        else if (data.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array) arr = rows;
        else return result;

        foreach (var r in arr.EnumerateArray())
        {
            var sym = r.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
            var typeRaw = (r.TryGetProperty("incomeType", out var it) ? it.GetString() ?? "" : "").ToUpperInvariant();
            var type = typeRaw switch
            {
                "REALIZED_PNL" => "realized_pnl",
                "COMMISSION"   => "commission",
                "FUNDING_FEE"  => "funding_fee",
                "TRANSFER"     => "transfer",
                _              => string.IsNullOrEmpty(typeRaw) ? "other" : typeRaw.ToLowerInvariant(),
            };
            var income = ParseDec(r, "income");
            var asset = r.TryGetProperty("asset", out var a) ? a.GetString() ?? "USDT" : "USDT";
            var tradeId = r.TryGetProperty("tradeId", out var tid) ? tid.ToString() : null;
            var tranId  = r.TryGetProperty("tranId",  out var trn) ? trn.ToString() : null;
            var time = r.TryGetProperty("time", out var t) && t.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
                : DateTime.UtcNow;

            result.Add(new PerpetualIncome
            {
                Symbol = sym, Exchange = ExchangeName,
                IncomeType = type, Income = income, Asset = asset,
                TradeId = string.IsNullOrEmpty(tradeId) ? null : tradeId,
                TranId  = string.IsNullOrEmpty(tranId)  ? null : tranId,
                Time = time,
            });
        }
        return result;
    }

    public async Task<List<PerpetualContract>> GetContractsAsync(CancellationToken ct = default)
    {
        // GET /openApi/swap/v2/quote/contracts （public、不需簽）
        // 回 array of { symbol, tradeMinQuantity, tradeMinUSDT, quantityPrecision,
        // maxLongLeverage, maxShortLeverage, currency, status, ... }
        var result = new List<PerpetualContract>();
        var resp = await _http.GetAsync($"{_baseUrl}/openApi/swap/v2/quote/contracts", ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json).RootElement;
        if (!IsOk(doc) || !doc.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return result;

        var now = DateTime.UtcNow;
        foreach (var item in data.EnumerateArray())
        {
            var sym = item.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(sym)) continue;

            var qtyPrecision = item.TryGetProperty("quantityPrecision", out var qp) && qp.TryGetInt32(out var qpI) ? qpI : 4;
            var qtyStep = (decimal)Math.Pow(10, -qtyPrecision);

            var maxLong  = item.TryGetProperty("maxLongLeverage",  out var mll) && mll.TryGetInt32(out var ml)  ? ml  : 0;
            var maxShort = item.TryGetProperty("maxShortLeverage", out var msl) && msl.TryGetInt32(out var ms)  ? ms  : 0;
            var maxLev   = Math.Max(maxLong, maxShort);
            if (maxLev <= 0) maxLev = 50;  // 保守 fallback、避免 cache 拿到 0

            // status: 1 = trading, 5 = pre-launch, etc. 只當 1 為 trading。
            var trading = !item.TryGetProperty("status", out var st) || (st.TryGetInt32(out var stI) ? stI == 1 : true);

            result.Add(new PerpetualContract
            {
                Symbol        = sym,
                MinQty        = ParseDec(item, "tradeMinQuantity"),
                QtyStep       = qtyStep,
                MinNotional   = ParseDec(item, "tradeMinUSDT"),
                MaxLeverage   = maxLev,
                QuoteCurrency = item.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "USDT" : "USDT",
                Trading       = trading,
                SnapshotAt    = now,
            });
        }
        return result;
    }

    public async Task<List<PerpetualTicker24h>> GetTickers24hAsync(CancellationToken ct = default)
    {
        // GET /openApi/swap/v2/quote/ticker （省略 symbol 參數 = 全部 USDT-M perp）
        // public endpoint、回 array of { symbol, lastPrice, openPrice, highPrice, lowPrice,
        // priceChange, priceChangePercent, volume, quoteVolume, ... }
        var result = new List<PerpetualTicker24h>();
        var resp = await _http.GetAsync($"{_baseUrl}/openApi/swap/v2/quote/ticker", ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(json).RootElement;
        if (!IsOk(doc) || !doc.TryGetProperty("data", out var data)) return result;

        // BingX 偶爾回 single object（指定 symbol 時），偶爾回 array（全部時）。兩種都接。
        var items = data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
            : new[] { data }.AsEnumerable().Select(x => x);

        foreach (var item in items)
        {
            var sym = item.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(sym)) continue;

            result.Add(new PerpetualTicker24h
            {
                Symbol             = sym,
                LastPrice          = ParseDec(item, "lastPrice"),
                HighPrice          = ParseDec(item, "highPrice"),
                LowPrice           = ParseDec(item, "lowPrice"),
                OpenPrice          = ParseDec(item, "openPrice"),
                Volume             = ParseDec(item, "volume"),
                QuoteVolume        = ParseDec(item, "quoteVolume"),
                PriceChange        = ParseDec(item, "priceChange"),
                PriceChangePercent = ParseDec(item, "priceChangePercent"),
                SnapshotAt         = DateTime.UtcNow,
            });
        }
        return result;
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static string MapOrderType(string t) => t.ToLowerInvariant() switch
    {
        "market"               => "MARKET",
        "limit"                => "LIMIT",
        "stop_market"          => "STOP_MARKET",
        "take_profit_market"   => "TAKE_PROFIT_MARKET",
        _                      => "MARKET",
    };

    private static string MapOrderStatus(string s) => s.ToUpperInvariant() switch
    {
        "NEW"               => "submitted",
        "PARTIALLY_FILLED"  => "partial",
        "FILLED"            => "filled",
        "CANCELED"          => "cancelled",
        "CANCELLED"         => "cancelled",
        "EXPIRED"           => "cancelled",
        "REJECTED"          => "rejected",
        _                   => "submitted",
    };

    private static decimal ParseDec(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el)) return 0m;
        return ParseDecValue(el);
    }

    private static decimal ParseDecValue(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out var d) ? d : 0m,
            JsonValueKind.String => decimal.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0m,
            _ => 0m,
        };
    }

    private static bool IsOk(JsonElement doc)
        => doc.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt32() == 0;

    private static void EnsureOk(JsonElement doc, string op)
    {
        if (IsOk(doc)) return;
        var msg = doc.TryGetProperty("msg", out var m) ? m.GetString() : "(no msg)";
        var code = doc.TryGetProperty("code", out var c) ? c.ToString() : "(no code)";
        throw new InvalidOperationException($"BingX {op} failed: code={code} msg={msg}");
    }

    /// <summary>
    /// POST 下單專用：簽章用「未編碼(raw)」的值算、URL 送「已編碼」的值。
    /// BingX 伺服器收到後是「先 url-decode 再驗章」→ 章必須對應 decode 後的字串；
    /// 但 URL 本身的值要編碼（takeProfit/stopLoss 的 JSON 含 {}":  不編碼會破壞 URL）。
    /// 兩者混用就會 code=100001 signature mismatch —— 正是 bracket SL/TP 開倉失敗主因。
    /// （GET 路徑一直是 raw 簽 raw 送、簡單值沒特殊字元所以一直正常。）
    /// 簽章與送出共用同一份順序（timestamp 殿後）；BingX 依收到順序重算、不需排序。
    /// </summary>
    private async Task<string> SignedPostFormAsync(string path, Dictionary<string, string> rawParams, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var ordered = rawParams.ToList();
        ordered.Add(new("timestamp", ts.ToString()));

        var rawStr = string.Join("&", ordered.Select(p => $"{p.Key}={p.Value}"));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawStr))).ToLower();

        var encStr = string.Join("&", ordered.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var content = new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded");
        var resp = await _http.PostAsync($"{_baseUrl}{path}?{encStr}&signature={sig}", content, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private string Sign(string query)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var qs = string.IsNullOrEmpty(query) ? $"timestamp={ts}" : $"{query}&timestamp={ts}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(qs));
        var signature = Convert.ToHexString(hash).ToLower();
        return $"{qs}&signature={signature}";
    }

    private async Task<string> SignedGetAsync(string path, string query, CancellationToken ct)
    {
        var signed = Sign(query);
        var resp = await _http.GetAsync($"{_baseUrl}{path}?{signed}", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> SignedPostAsync(string path, string query, CancellationToken ct)
    {
        var signed = Sign(query);
        var content = new StringContent("", Encoding.UTF8, "application/x-www-form-urlencoded");
        var resp = await _http.PostAsync($"{_baseUrl}{path}?{signed}", content, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<string> SignedDeleteAsync(string path, string query, CancellationToken ct)
    {
        var signed = Sign(query);
        var resp = await _http.DeleteAsync($"{_baseUrl}{path}?{signed}", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }
}
