using System.Text.Json;
using Broker.Helpers;
using Broker.Middleware;
using BrokerCore.Contracts;
using BrokerCore.Services;
using FunctionPool.Registry;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Broker.Endpoints;

/// <summary>
/// 永續合約 API（BingX perpetual swap）。
/// 跟既有 /api/v1/trading/* 故意分開：spot 跟 perp 是不同帳戶體系、不能混用。
///
/// Routes:
///   GET  /api/v1/perpetual/exchanges            列出已連線的 perpetual exchanges
///   GET  /api/v1/perpetual/account?exchange=bingx
///   GET  /api/v1/perpetual/positions?exchange=bingx
///   POST /api/v1/perpetual/order                {exchange, symbol, side, position_side, order_type, quantity, leverage,
///                                                 take_profit_price?, stop_loss_price?, ...}
///     C3 — 若帶 take_profit_price / stop_loss_price 會走 bracket order（BingX 自動 attach
///     TP/SL 到 position、atomic）、broker crash 不會留裸位。
///     兩個都 nullable、null = 不送 bracket、走原本流程。
///   DELETE /api/v1/perpetual/order              ?exchange=bingx&symbol=BTC-USDT&order_id=12345
///   GET  /api/v1/perpetual/order                ?exchange&symbol&order_id (status query)
///   GET  /api/v1/perpetual/open-orders?exchange=bingx[&symbol=BTC-USDT]
///   POST /api/v1/perpetual/leverage             {exchange, symbol, position_side, leverage}
///   GET  /api/v1/perpetual/mark-price?exchange=bingx&symbol=BTC-USDT
/// </summary>
public static class PerpetualEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var p = group.MapGroup("/perpetual");

        p.MapGet("/exchanges", async (IWorkerRegistry registry, IExecutionDispatcher dispatcher, HttpContext ctx) =>
        {
            if (!registry.HasAvailableWorker("trading.perpetual"))
                return Results.Ok(ApiResponseHelper.Error("trading-worker not connected (or perpetual not enabled)"));
            var r = await dispatcher.DispatchAsync(Build(ctx, "trading.perpetual", "list_exchanges", "{}"));
            return ToResponse(r);
        });

        p.MapGet("/account", async (IWorkerRegistry registry, IExecutionDispatcher dispatcher, HttpRequest req) =>
        {
            if (RequireLogin(req.HttpContext) is { } denied) return denied;
            if (!registry.HasAvailableWorker("trading.perpetual"))
                return Results.Ok(ApiResponseHelper.Error("trading-worker not connected"));
            var ex = req.Query.TryGetValue("exchange", out var e) ? e.ToString() : "bingx";
            var r = await dispatcher.DispatchAsync(Build(req.HttpContext, "trading.perpetual", "get_account", JsonSerializer.Serialize(new { exchange = ex })));
            return ToResponse(r);
        });

        p.MapGet("/positions", async (IWorkerRegistry registry, IExecutionDispatcher dispatcher, HttpRequest req) =>
        {
            if (RequireLogin(req.HttpContext) is { } denied) return denied;
            if (!registry.HasAvailableWorker("trading.perpetual"))
                return Results.Ok(ApiResponseHelper.Error("trading-worker not connected"));
            var ex = req.Query.TryGetValue("exchange", out var e) ? e.ToString() : "bingx";
            var r = await dispatcher.DispatchAsync(Build(req.HttpContext, "trading.perpetual", "get_positions", JsonSerializer.Serialize(new { exchange = ex })));
            return ToResponse(r);
        });

        p.MapPost("/order", async (IWorkerRegistry registry, IExecutionDispatcher dispatcher, HttpRequest req) =>
        {
            if (RequireLogin(req.HttpContext) is { } denied) return denied;
            if (!registry.HasAvailableWorker("trading.perpetual"))
                return Results.Ok(ApiResponseHelper.Error("trading-worker not connected"));
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();

            // ★ Pre-flight：在 approval / dispatch 前先擋掉違反 symbol 規格的單。
            // 之前的流程是「approval → 派發 → 交易所拒」整條路徑都跑、user 收到的是「approved 但失敗」訊息。
            // 改成這裡就 400、不創建 approval、不打交易所。
            try
            {
                var doc = JsonDocument.Parse(body).RootElement;
                var exch  = doc.TryGetProperty("exchange",  out var e) ? e.GetString() ?? "" : "";
                var sym   = doc.TryGetProperty("symbol",    out var s) ? s.GetString() ?? "" : "";
                var qty   = doc.TryGetProperty("quantity",  out var q) ? q.GetDecimal() : 0m;
                var lev   = doc.TryGetProperty("leverage",  out var lv) && lv.TryGetInt32(out var lvi) ? lvi : 1;
                var reduceOnly = doc.TryGetProperty("reduce_only", out var ro) && ro.GetBoolean();
                // reduce_only = 平倉，不檢查 min（要平多少平多少）
                if (!reduceOnly)
                {
                    var (ok, err, _) = BrokerCore.Trading.SymbolSpecs.PreflightOrder(exch, sym, qty, lev);
                    if (!ok)
                        return Results.BadRequest(ApiResponseHelper.Error($"pre-flight: {err}"));
                }

                // finding F:人工下單冪等。自動路徑(AutoTrader)早有 client_order_id 去重(BingX 101400 擋重複),
                // 但人工 POST /order 原本裸傳 body、沒帶 key → 重試 / 連點 / 前端重送 = 重複下真錢單。
                // caller 沒自帶 client_order_id 就注入 deterministic key(m- 前綴、5 分鐘桶、含 reduceOnly 分流開/平)。
                // 注入發生在 DispatchAsync 前 → key 進派發 payload、即使重派 BingX 仍以 101400 擋第二單。
                // 緊急可設 PERP_MANUAL_IDEMPOTENT_KEYS=0 關閉(預設啟用、對齊自動路徑 finding K)。
                if (Environment.GetEnvironmentVariable("PERP_MANUAL_IDEMPOTENT_KEYS")
                        is not ("0" or "false" or "False" or "FALSE" or "off" or "OFF"))
                {
                    var hasCaller = doc.TryGetProperty("client_order_id", out var cidEl)
                        && cidEl.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cidEl.GetString());
                    if (!hasCaller)  // 尊重 caller 自己的冪等鍵、不覆蓋
                    {
                        var owner   = RequestBodyHelper.GetPrincipalId(req.HttpContext);
                        var sideStr = doc.TryGetProperty("side",          out var sd) ? sd.GetString() ?? "" : "";
                        var psStr   = doc.TryGetProperty("position_side", out var ps) ? ps.GetString() ?? "" : "";
                        var bucket  = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300;
                        var key     = DeriveManualOrderKey(owner, exch, sym, sideStr, psStr, qty, reduceOnly, bucket);
                        // 用 Dictionary<string,JsonElement> round-trip、保留 __credentials / tp / sl 等所有原欄位與型別
                        var map = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? new();
                        map["client_order_id"] = JsonSerializer.SerializeToElement(key);
                        body = JsonSerializer.Serialize(map);
                    }
                }
            }
            catch (JsonException) { /* body parse 失敗 → 不注入、讓 worker 端處理 */ }

            var r = await dispatcher.DispatchAsync(Build(req.HttpContext, "trading.perpetual", "place_order", body));
            return ToResponse(r);
        });

        p.MapDelete("/order", async (IWorkerRegistry registry, IExecutionDispatcher dispatcher, HttpRequest req) =>
        {
            if (RequireLogin(req.HttpContext) is { } denied) return denied;
            if (!registry.HasAvailableWorker("trading.perpetual"))
                return Results.Ok(ApiResponseHelper.Error("trading-worker not connected"));
            var ex = req.Query.TryGetValue("exchange", out var e) ? e.ToString() : "bingx";
            var sym = req.Query.TryGetValue("symbol", out var s) ? s.ToString() : "";
            var oid = req.Query.TryGetValue("order_id", out var o) ? o.ToString() : "";
            var payload = JsonSerializer.Serialize(new { exchange = ex, symbol = sym, order_id = oid });
            var r = await dispatcher.DispatchAsync(Build(req.HttpContext, "trading.perpetual", "cancel_order", payload));
            return ToResponse(r);
        });

        p.MapGet("/order", async (IWorkerRegistry registry, IExecutionDispatcher dispatcher, HttpRequest req) =>
        {
            if (RequireLogin(req.HttpContext) is { } denied) return denied;
            if (!registry.HasAvailableWorker("trading.perpetual"))
                return Results.Ok(ApiResponseHelper.Error("trading-worker not connected"));
            var ex = req.Query.TryGetValue("exchange", out var e) ? e.ToString() : "bingx";
            var sym = req.Query.TryGetValue("symbol", out var s) ? s.ToString() : "";
            var oid = req.Query.TryGetValue("order_id", out var o) ? o.ToString() : "";
            var payload = JsonSerializer.Serialize(new { exchange = ex, symbol = sym, order_id = oid });
            var r = await dispatcher.DispatchAsync(Build(req.HttpContext, "trading.perpetual", "get_order", payload));
            return ToResponse(r);
        });

        p.MapGet("/open-orders", async (IWorkerRegistry registry, IExecutionDispatcher dispatcher, HttpRequest req) =>
        {
            if (RequireLogin(req.HttpContext) is { } denied) return denied;
            if (!registry.HasAvailableWorker("trading.perpetual"))
                return Results.Ok(ApiResponseHelper.Error("trading-worker not connected"));
            var ex = req.Query.TryGetValue("exchange", out var e) ? e.ToString() : "bingx";
            var sym = req.Query.TryGetValue("symbol", out var s) ? s.ToString() : null;
            var payload = JsonSerializer.Serialize(new { exchange = ex, symbol = sym });
            var r = await dispatcher.DispatchAsync(Build(req.HttpContext, "trading.perpetual", "get_open_orders", payload));
            return ToResponse(r);
        });

        p.MapPost("/leverage", async (IWorkerRegistry registry, IExecutionDispatcher dispatcher, HttpRequest req) =>
        {
            if (RequireLogin(req.HttpContext) is { } denied) return denied;
            if (!registry.HasAvailableWorker("trading.perpetual"))
                return Results.Ok(ApiResponseHelper.Error("trading-worker not connected"));
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            var r = await dispatcher.DispatchAsync(Build(req.HttpContext, "trading.perpetual", "set_leverage", body));
            return ToResponse(r);
        });

        p.MapGet("/mark-price", async (IWorkerRegistry registry, IExecutionDispatcher dispatcher, HttpRequest req) =>
        {
            if (!registry.HasAvailableWorker("trading.perpetual"))
                return Results.Ok(ApiResponseHelper.Error("trading-worker not connected"));
            var ex = req.Query.TryGetValue("exchange", out var e) ? e.ToString() : "bingx";
            var sym = req.Query.TryGetValue("symbol", out var s) ? s.ToString() : "";
            var payload = JsonSerializer.Serialize(new { exchange = ex, symbol = sym });
            var r = await dispatcher.DispatchAsync(Build(req.HttpContext, "trading.perpetual", "get_mark_price", payload));
            return ToResponse(r);
        });
    }

    /// <summary>
    /// finding F — 人工下單的 deterministic 冪等 key（純函式、好測,比照 AutoTraderService.DeriveIdemKey）。
    /// 同一意圖（owner|exchange|symbol|side|positionSide|qty|reduceOnly + bucket）→ 同 key → BingX 以 101400 擋重複。
    /// reduceOnly 納入 basis → 開倉與平倉即使同 symbol/side 也分流;m- 前綴(manual)、≤36 char、不撞自動的 op-/cl-/-sl。
    /// </summary>
    public static string DeriveManualOrderKey(
        string owner, string exchange, string symbol, string side, string positionSide,
        decimal quantity, bool reduceOnly, long bucket)
    {
        var basis = $"{owner}|{exchange}|{symbol}|{side}|{positionSide}|{quantity:G}|{reduceOnly}|{bucket}";
        var h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(basis)));
        return $"m-{h.Substring(0, 16).ToLowerInvariant()}";
    }

    /// <summary>
    /// 從 HttpContext 抓真實的 principal/role/task/session（由 BrokerAuthMiddleware /
    /// InternalBotAuthMiddleware 注入）。
    /// 重要：絕對不能硬寫 PrincipalId="system"——PoolDispatcher 的 approval gate 對
    /// "system" 完全放行（讓 AutoTrader 等內部背景任務跑），任何走 HTTP 進來的單都
    /// 必須帶真實身份、否則 admin 核准就被繞過了。
    /// 沒帶 auth 的 case（極少、純內部測試）才 fallback "system"。
    /// </summary>
    // 2026-06-05 安全:perpetual 路由全要登入。原本未認證 → Build() 退 principal="system" →
    // PoolDispatcher approval gate 對 "system" 放行 → 繞過人工審核、直接下真錢 perp 單/改槓桿。
    // 比照 TradingEndpoints 既有擋法(空 principal → 401)。dashboard cookie / scoped_token 任一登入即放行。
    private static IResult? RequireLogin(HttpContext ctx) =>
        string.IsNullOrEmpty(RequestBodyHelper.GetPrincipalId(ctx))
            ? Results.Json(ApiResponseHelper.Error("Login required", 401), statusCode: 401)
            : null;

    private static ApprovedRequest Build(HttpContext ctx, string capability, string route, string payload)
    {
        // 用 RequestBodyHelper 統一兩套 auth：scoped_token + cookie session 都認
        var principalId = RequestBodyHelper.GetPrincipalId(ctx);
        var roleId      = RequestBodyHelper.GetRoleId(ctx);
        if (string.IsNullOrEmpty(principalId)) principalId = "system";
        if (string.IsNullOrEmpty(roleId))      roleId      = "system";
        var taskId      = ctx.Items[BrokerAuthMiddleware.TaskIdKey]    as string ?? "perpetual-api";
        var sessionId   = ctx.Items[BrokerAuthMiddleware.SessionIdKey] as string ?? "perpetual-api";
        return new ApprovedRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            CapabilityId = capability, Route = route, Payload = payload,
            Scope = "{}", PrincipalId = principalId, Role = roleId,
            TaskId = taskId, SessionId = sessionId,
        };
    }

    // 走 ApprovalAwareResponseHelper：approval gate 卡住的單會被重塑成 status="pending_approval"
    // 結構化回應、不是「失敗 + 字串」、讓 dashboard / bot 兩端都能分得出「真失敗 vs 卡審」。
    private static IResult ToResponse(BrokerCore.Contracts.ExecutionResult result)
        => ApprovalAwareResponseHelper.Shape(result);
}
