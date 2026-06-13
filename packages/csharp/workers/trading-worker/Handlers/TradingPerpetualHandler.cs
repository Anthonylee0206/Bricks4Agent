using System.Text.Json;
using TradingWorker.Exchange;
using TradingWorker.Models;
using TradingWorker.Storage;
using WorkerSdk;

namespace TradingWorker.Handlers;

/// <summary>
/// trading.perpetual capability — 統一處理永續合約相關呼叫。
/// 跟 spot 的 trading.account / trading.order 故意分開、避免兩種帳戶資料互相污染。
///
/// Routes:
///   - get_account     -> exchange's perpetual balance + margin + unrealized
///   - get_positions   -> open positions（雙向、含 leverage / liq price）
///   - place_order     -> 下單（open/close long/short）
///   - set_position_sl -> 移動 position 的 exchange-side stop（broker BE/trailing 同步用）
///   - cancel_order    -> 取消未成交
///   - get_order       -> 查詢單筆訂單
///   - get_open_orders -> 列出 open orders
///   - set_leverage    -> 設定該 symbol 該方向槓桿
///   - get_mark_price  -> 取 mark price（不需簽名、給 dashboard 算 PnL 用）
///   - list_exchanges  -> 列出已啟用的 perpetual exchanges
/// </summary>
public class TradingPerpetualHandler : ICapabilityHandler
{
    private readonly Dictionary<string, IPerpetualClient> _clients;
    private readonly TradingDbStorage? _db;
    private readonly Microsoft.Extensions.Logging.ILoggerFactory? _loggerFactory;

    // ── ad-hoc per-user client cache（A2.5b 2026-05-10）──
    // 用 (exchange, sha256(apiKey)[:16], isDemo) 當 key、避免每筆請求 rebuild HttpClient + BingxClient。
    // 沒有 TTL——容器活著就快取在；容器重啟自動清。同 user 兩支 key 切換時各自一個 entry、互不干擾。
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IPerpetualClient> _adhocClients = new();

    public string CapabilityId => "trading.perpetual";

    public TradingPerpetualHandler(Dictionary<string, IPerpetualClient> clients,
        Microsoft.Extensions.Logging.ILoggerFactory? loggerFactory = null,
        TradingDbStorage? db = null)
    {
        _clients = clients;
        _loggerFactory = loggerFactory;
        _db = db;
    }

    public async Task<(bool Success, string? ResultPayload, string? Error)> ExecuteAsync(
        string requestId, string route, string payload, string scope, CancellationToken ct)
    {
        var opts = string.IsNullOrWhiteSpace(payload)
            ? new JsonElement()
            : JsonDocument.Parse(payload).RootElement;

        return route switch
        {
            "get_account"      => await GetAccount(opts, ct),
            "get_positions"    => await GetPositions(opts, ct),
            "place_order"      => await PlaceOrder(opts, ct),
            "set_position_sl"  => await SetPositionSl(opts, ct),
            "cancel_order"     => await CancelOrder(opts, ct),
            "get_order"        => await GetOrder(opts, ct),
            "get_open_orders"  => await GetOpenOrders(opts, ct),
            "set_leverage"     => await SetLeverage(opts, ct),
            "get_mark_price"   => await GetMarkPrice(opts, ct),
            "get_tickers_24h"  => await GetTickers24h(opts, ct),
            "get_contracts"    => await GetContracts(opts, ct),
            "list_exchanges"   => ListExchanges(),
            _ => (false, null, $"Unknown route: {route}")
        };
    }

    private async Task<(bool, string?, string?)> GetTickers24h(JsonElement opts, CancellationToken ct)
    {
        if (!TryGetClient(opts, out var c, out var err)) return (false, null, err);
        try
        {
            var list = await c!.GetTickers24hAsync(ct);
            var json = JsonSerializer.Serialize(new
            {
                count = list.Count,
                snapshot_at = list.FirstOrDefault()?.SnapshotAt ?? DateTime.UtcNow,
                tickers = list.Select(t => new
                {
                    symbol = t.Symbol,
                    last_price = t.LastPrice,
                    high = t.HighPrice,
                    low = t.LowPrice,
                    open = t.OpenPrice,
                    volume = t.Volume,
                    quote_volume = t.QuoteVolume,
                    price_change = t.PriceChange,
                    price_change_pct = t.PriceChangePercent,
                })
            });
            return (true, json, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private async Task<(bool, string?, string?)> GetContracts(JsonElement opts, CancellationToken ct)
    {
        if (!TryGetClient(opts, out var c, out var err)) return (false, null, err);
        try
        {
            var list = await c!.GetContractsAsync(ct);
            var json = JsonSerializer.Serialize(new
            {
                count = list.Count,
                snapshot_at = list.FirstOrDefault()?.SnapshotAt ?? DateTime.UtcNow,
                contracts = list.Select(p => new
                {
                    symbol         = p.Symbol,
                    min_qty        = p.MinQty,
                    qty_step       = p.QtyStep,
                    min_notional   = p.MinNotional,
                    max_leverage   = p.MaxLeverage,
                    quote_currency = p.QuoteCurrency,
                    trading        = p.Trading,
                })
            });
            return (true, json, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private (bool, string?, string?) ListExchanges()
    {
        var exchanges = _clients.Select(kv => new
        {
            exchange = kv.Key,
            is_demo  = kv.Value.IsDemo,
        }).ToList();
        return (true, JsonSerializer.Serialize(new { exchanges }), null);
    }

    private async Task<(bool, string?, string?)> GetAccount(JsonElement opts, CancellationToken ct)
    {
        if (!TryGetClient(opts, out var c, out var err)) return (false, null, err);
        try
        {
            var acc = await c!.GetAccountAsync(ct);
            var json = JsonSerializer.Serialize(new
            {
                exchange = acc.Exchange,
                account_id = acc.AccountId,
                currency = acc.Currency,
                balance = acc.Balance,
                equity = acc.Equity,
                unrealized_pnl = acc.UnrealizedPnl,
                margin_used = acc.MarginUsed,
                available_margin = acc.AvailableMargin,
                open_positions_count = acc.OpenPositionsCount,
                is_demo = acc.IsDemo,
                updated_at = acc.UpdatedAt,
            });
            return (true, json, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private async Task<(bool, string?, string?)> GetPositions(JsonElement opts, CancellationToken ct)
    {
        if (!TryGetClient(opts, out var c, out var err)) return (false, null, err);
        try
        {
            var list = await c!.GetPositionsAsync(ct);
            var json = JsonSerializer.Serialize(new
            {
                positions = list.Select(p => new
                {
                    symbol = p.Symbol, exchange = p.Exchange, side = p.Side,
                    quantity = p.Quantity, avg_entry_price = p.AvgEntryPrice,
                    mark_price = p.MarkPrice, unrealized_pnl = p.UnrealizedPnl,
                    unrealized_pnl_pct = p.UnrealizedPnlPercent,
                    leverage = p.Leverage, margin_mode = p.MarginMode, margin_used = p.MarginUsed,
                    liquidation_price = p.LiquidationPrice,
                    liquidation_distance_pct = p.LiquidationDistancePct,
                    updated_at = p.UpdatedAt,
                })
            });
            return (true, json, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private async Task<(bool, string?, string?)> PlaceOrder(JsonElement opts, CancellationToken ct)
    {
        if (!TryGetClient(opts, out var c, out var err)) return (false, null, err);

        var symbol       = opts.TryGetProperty("symbol", out var s)       ? s.GetString() ?? "" : "";
        var side         = opts.TryGetProperty("side", out var sd)        ? sd.GetString() ?? "" : "";
        var positionSide = opts.TryGetProperty("position_side", out var ps) ? ps.GetString() ?? "" : "";
        var orderType    = opts.TryGetProperty("order_type", out var ot)  ? ot.GetString() ?? "market" : "market";
        var qty          = opts.TryGetProperty("quantity", out var q)     ? q.GetDecimal() : 0m;
        var leverage     = opts.TryGetProperty("leverage", out var lv) && lv.TryGetInt32(out var lvI) ? lvI : 1;
        var reduceOnly   = opts.TryGetProperty("reduce_only", out var ro) && ro.GetBoolean();
        var strategy     = opts.TryGetProperty("strategy", out var stg) && stg.ValueKind == JsonValueKind.String ? stg.GetString() : null;
        decimal? limitPrice = opts.TryGetProperty("limit_price", out var lp) && lp.ValueKind == JsonValueKind.Number ? lp.GetDecimal() : null;
        decimal? stopPrice  = opts.TryGetProperty("stop_price", out var sp) && sp.ValueKind == JsonValueKind.Number ? sp.GetDecimal() : null;
        // C3 — Bracket order：開倉時可選擇帶 TP/SL、BingX 自動 attach 到 position（atomic）。
        // payload field: take_profit_price / stop_loss_price。null 視為不送、走傳統流程。
        decimal? tpPrice = opts.TryGetProperty("take_profit_price", out var tp) && tp.ValueKind == JsonValueKind.Number ? tp.GetDecimal() : null;
        decimal? slPrice = opts.TryGetProperty("stop_loss_price",   out var sl) && sl.ValueKind == JsonValueKind.Number ? sl.GetDecimal() : null;
        // 冪等:caller 帶 client_order_id 就拿來當 OrderId（→ BingX clientOrderID）、沒帶才自己生隨機 id。
        // failover 重送同一 deterministic client_order_id → BingX 擋重複（code=101400、PlaceOrderAsync 視為冪等命中）。
        var clientOrderId = opts.TryGetProperty("client_order_id", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetString() : null;

        if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(side) || string.IsNullOrEmpty(positionSide) || qty <= 0m)
            return (false, null, "missing required: symbol/side/position_side/quantity");

        try
        {
            // 開倉前先設 leverage（BingX 是 sticky 的、設一次就生效；但 idempotent、再設沒副作用）
            if (!reduceOnly && leverage > 1)
                await c!.SetLeverageAsync(symbol, positionSide, leverage, ct);

            var order = new PerpetualOrder
            {
                OrderId = !string.IsNullOrWhiteSpace(clientOrderId) ? clientOrderId! : $"perp-{Guid.NewGuid():N}"[..16],
                Symbol = symbol, Exchange = c!.ExchangeName, Side = side, PositionSide = positionSide,
                OrderType = orderType, Quantity = qty,
                LimitPrice = limitPrice, StopPrice = stopPrice,
                TakeProfitPrice = tpPrice, StopLossPrice = slPrice,
                Leverage = leverage, ReduceOnly = reduceOnly,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };

            var result = await c.PlaceOrderAsync(order, ct);

            // Persist 永續成交到 local DB（給 get_trade_history / pnl-summary 用）。
            // BingX market order 通常即時成交、result 會帶 FilledQty + FilledPrice。
            // 開倉只有手續費沒 realized_pnl；reduce_only 平倉才有 realized_pnl。
            // finding I:filled_no_sl(進場成交但獨立 SL 失敗)也是「已成交」→ 必須一併記錄,
            // 否則這筆有倉、無 SL 的 fill 不進 DB → get_trade_history/pnl-summary 缺一筆(broker soft-SL 仍會接管部位)。
            if (_db != null && (result.Status == "filled" || result.Status == "filled_no_sl")
                && result.FilledQty > 0 && result.FilledPrice.HasValue)
            {
                try
                {
                    _db.SaveTrade(new TradeRecord
                    {
                        TradeId    = $"perp-{result.ExternalId}",
                        OrderId    = result.OrderId,
                        Symbol     = result.Symbol,
                        Exchange   = result.Exchange,
                        Side       = result.Side,
                        Quantity   = result.FilledQty,
                        Price      = result.FilledPrice.Value,
                        Fee        = null,                 // BingX 預設不在 place_order response 帶 fee
                        RealizedPnl = null,                // realized PnL 在 FillPoller 從 income endpoint 補抓
                        ExecutedAt = result.FilledAt ?? DateTime.UtcNow,
                        Strategy   = strategy,             // AutoTrader 開倉時帶、手動為 null；之後 perp-income row 會 inherit
                    });
                }
                catch { /* persist 失敗別影響 caller */ }
            }

            var json = JsonSerializer.Serialize(SerializeOrder(result));
            return (true, json, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    /// <summary>
    /// #1 — broker BE/trailing 同步：把 position 的 exchange-side stop 移到新價。
    /// 安全順序：先 snapshot 既有 stop → 先下新 stop（避免 cancel 後 place 失敗留裸位）→ 再 cancel 舊 stop。
    /// 只動 STOP 類 reduce 單、不碰 TAKE_PROFIT。失敗回 error 由 broker 端 log（軟 SL 仍生效）。
    /// </summary>
    private async Task<(bool, string?, string?)> SetPositionSl(JsonElement opts, CancellationToken ct)
    {
        if (!TryGetClient(opts, out var c, out var err)) return (false, null, err);

        var symbol       = opts.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
        var positionSide = opts.TryGetProperty("position_side", out var ps) ? ps.GetString() ?? "" : "";
        var closeSide    = opts.TryGetProperty("close_side", out var cs) ? cs.GetString() ?? "" : "";
        var qty          = opts.TryGetProperty("quantity", out var q) ? q.GetDecimal() : 0m;
        var slPrice      = opts.TryGetProperty("stop_loss_price", out var sl) && sl.ValueKind == JsonValueKind.Number ? sl.GetDecimal() : 0m;

        if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(positionSide) || string.IsNullOrEmpty(closeSide) || qty <= 0m || slPrice <= 0m)
            return (false, null, "missing required: symbol/position_side/close_side/quantity/stop_loss_price");

        try
        {
            // 1) snapshot 既有 STOP 類 reduce 單（同 positionSide）——之後 cancel 這批
            var existing = await c!.GetOpenOrdersAsync(symbol, ct);
            var oldStopIds = existing
                .Where(o => !string.IsNullOrEmpty(o.ExternalId)
                    && o.OrderType.Contains("stop", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(o.PositionSide, positionSide, StringComparison.OrdinalIgnoreCase))
                .Select(o => o.ExternalId!)
                .ToList();

            // 2) 先下新 stop（reduce-only STOP_MARKET）——失敗就直接回 error、不動舊單（位仍有舊 stop 保護）
            var newStop = new PerpetualOrder
            {
                OrderId = $"perp-{Guid.NewGuid():N}"[..16],
                Symbol = symbol, Exchange = c!.ExchangeName, Side = closeSide, PositionSide = positionSide,
                OrderType = "stop_market", Quantity = qty, StopPrice = slPrice, ReduceOnly = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            var placed = await c.PlaceOrderAsync(newStop, ct);

            // 3) 再 cancel 舊 stop（排除剛下的那張）——cancel 失敗只記、不影響「新 stop 已就位」的結果
            var cancelled = new List<string>();
            var cancelErrors = new List<string>();
            foreach (var id in oldStopIds)
            {
                if (id == placed.ExternalId) continue;
                try { await c.CancelOrderAsync(symbol, id, ct); cancelled.Add(id); }
                catch (Exception cex) { cancelErrors.Add($"{id}: {cex.Message}"); }
            }

            var json = JsonSerializer.Serialize(new
            {
                symbol, position_side = positionSide, stop_loss_price = slPrice,
                new_stop_id = placed.ExternalId, new_stop_status = placed.Status,
                cancelled_old = cancelled, cancel_errors = cancelErrors,
            });
            return (true, json, null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private async Task<(bool, string?, string?)> CancelOrder(JsonElement opts, CancellationToken ct)
    {
        if (!TryGetClient(opts, out var c, out var err)) return (false, null, err);
        var symbol = opts.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
        var orderId = opts.TryGetProperty("order_id", out var oid) ? oid.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(symbol) || string.IsNullOrEmpty(orderId))
            return (false, null, "missing required: symbol/order_id");
        try
        {
            var r = await c!.CancelOrderAsync(symbol, orderId, ct);
            return (true, JsonSerializer.Serialize(SerializeOrder(r)), null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private async Task<(bool, string?, string?)> GetOrder(JsonElement opts, CancellationToken ct)
    {
        if (!TryGetClient(opts, out var c, out var err)) return (false, null, err);
        var symbol = opts.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
        var orderId = opts.TryGetProperty("order_id", out var oid) ? oid.GetString() ?? "" : "";
        try
        {
            var r = await c!.GetOrderStatusAsync(symbol, orderId, ct);
            if (r == null) return (false, null, "order not found");
            return (true, JsonSerializer.Serialize(SerializeOrder(r)), null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private async Task<(bool, string?, string?)> GetOpenOrders(JsonElement opts, CancellationToken ct)
    {
        if (!TryGetClient(opts, out var c, out var err)) return (false, null, err);
        var symbol = opts.TryGetProperty("symbol", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        try
        {
            var list = await c!.GetOpenOrdersAsync(symbol, ct);
            return (true, JsonSerializer.Serialize(new { orders = list.Select(SerializeOrder) }), null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private async Task<(bool, string?, string?)> SetLeverage(JsonElement opts, CancellationToken ct)
    {
        if (!TryGetClient(opts, out var c, out var err)) return (false, null, err);
        var symbol = opts.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
        var positionSide = opts.TryGetProperty("position_side", out var ps) ? ps.GetString() ?? "long" : "long";
        var leverage = opts.TryGetProperty("leverage", out var lv) && lv.TryGetInt32(out var i) ? i : 1;
        if (string.IsNullOrEmpty(symbol) || leverage < 1) return (false, null, "missing/invalid: symbol/leverage");
        var ok = await c!.SetLeverageAsync(symbol, positionSide, leverage, ct);
        return (true, JsonSerializer.Serialize(new { symbol, position_side = positionSide, leverage, ok }), null);
    }

    private async Task<(bool, string?, string?)> GetMarkPrice(JsonElement opts, CancellationToken ct)
    {
        if (!TryGetClient(opts, out var c, out var err)) return (false, null, err);
        var symbol = opts.TryGetProperty("symbol", out var s) ? s.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(symbol)) return (false, null, "missing: symbol");
        try
        {
            var price = await c!.GetMarkPriceAsync(symbol, ct);
            return (true, JsonSerializer.Serialize(new { symbol, mark_price = price }), null);
        }
        catch (Exception ex) { return (false, null, ex.Message); }
    }

    private bool TryGetClient(JsonElement opts, out IPerpetualClient? c, out string? err)
    {
        var exchange = opts.TryGetProperty("exchange", out var e) ? e.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(exchange)) exchange = _clients.Keys.FirstOrDefault() ?? "";

        // A2.5b：payload 帶 __credentials 就用 user 自己的 key 建 ad-hoc client；不帶就 fallback env-config 預設。
        if (opts.TryGetProperty("__credentials", out var creds) && creds.ValueKind == JsonValueKind.Object)
        {
            var apiKey = creds.TryGetProperty("api_key", out var k) ? k.GetString() ?? "" : "";
            var apiSecret = creds.TryGetProperty("api_secret", out var s) ? s.GetString() ?? "" : "";
            var isDemo = creds.TryGetProperty("is_demo", out var d) && d.GetBoolean();
            if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(apiSecret))
            {
                c = ResolveAdhocClient(exchange, apiKey, apiSecret, isDemo);
                err = null;
                return c != null || (err = $"no ad-hoc support for exchange '{exchange}'") == null;
            }
        }

        if (!_clients.TryGetValue(exchange, out var found))
        {
            c = null; err = $"perpetual exchange not configured: {exchange}";
            return false;
        }
        c = found; err = null;
        return true;
    }

    private IPerpetualClient? ResolveAdhocClient(string exchange, string apiKey, string apiSecret, bool isDemo)
    {
        // 不洩漏 api_key 完整值給 cache key——hash 一次取前 16 byte hex 就夠抗碰撞
        using var sha = System.Security.Cryptography.SHA256.Create();
        var keyHash = Convert.ToHexString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(apiKey)))[..16].ToLowerInvariant();
        var cacheKey = $"{exchange.ToLowerInvariant()}:{keyHash}:{isDemo}";

        // Cache hit 直接回；未命中才 build。未知交易所 build 會回 null、不快取（避免之後永遠拿到 null）
        if (_adhocClients.TryGetValue(cacheKey, out var hit)) return hit;
        var built = BuildAdhocClient(exchange, apiKey, apiSecret, isDemo);
        if (built == null) return null;
        return _adhocClients.GetOrAdd(cacheKey, built);
    }

    private IPerpetualClient? BuildAdhocClient(string exchange, string apiKey, string apiSecret, bool isDemo)
    {
        var ex = exchange.ToLowerInvariant();
        if (ex == "bingx")
        {
            // 重新 new 一個 HttpClient — BingxPerpetualClient 自己持有、不跟其他 client 共享 connection pool
            // 是必要的（不然 cancel 一個會牽連到別人）
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var logger = _loggerFactory != null
                ? Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<TradingWorker.Exchange.BingxPerpetualClient>(_loggerFactory)
                : Microsoft.Extensions.Logging.Abstractions.NullLogger<TradingWorker.Exchange.BingxPerpetualClient>.Instance;
            return new TradingWorker.Exchange.BingxPerpetualClient(http, logger, apiKey, apiSecret, isDemo);
        }
        // binance / alpaca: 之後實作（Phase A2.5c 之類）
        return null;
    }

    private static object SerializeOrder(PerpetualOrder o) => new
    {
        order_id = o.OrderId, symbol = o.Symbol, exchange = o.Exchange,
        side = o.Side, position_side = o.PositionSide,
        order_type = o.OrderType, quantity = o.Quantity,
        limit_price = o.LimitPrice, stop_price = o.StopPrice,
        leverage = o.Leverage, reduce_only = o.ReduceOnly,
        status = o.Status, filled_qty = o.FilledQty, filled_price = o.FilledPrice,
        external_id = o.ExternalId, error = o.Error,
        created_at = o.CreatedAt, filled_at = o.FilledAt, updated_at = o.UpdatedAt,
    };
}
