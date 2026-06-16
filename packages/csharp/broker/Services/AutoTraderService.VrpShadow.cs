using System.Text.Json;
using BrokerCore.Models;
using Microsoft.Extensions.Logging;

namespace Broker.Services;

/// <summary>
/// AutoTraderService 的 VRP / 波動 carry shadow 部分(partial、模組化、ADR 層 2 可整塊抽成獨立 worker)。
/// 每 cycle:結算到期 roll 腿 + roll 邊界開新腿。**只讀行情(quote.ohlcv)+ 寫 vrp_shadow_legs(shadow=true)、
/// 不下任何單 → 對真錢零路徑接觸**。純決策走 VrpShadowRecorder/VrpShadowMath(已測)、本檔只做 I/O 接線。
/// BTC only、always-on(不擇時)、size 1.0 baseline、condor 封 −50%。見 docs/designs/vrp-shadow-deploy-sketch.md。
/// </summary>
public partial class AutoTraderService
{
    private const string  VrpCurrency       = "BTC";
    private const int     VrpHorizonDays    = 30;
    private const decimal VrpCapPct         = 50m;
    private const decimal VrpSizeScalar     = 1.0m;     // 平台 baseline;∝DVOL refinement 屬私有 alpha
    private const decimal VrpSleeveNotional = 1000m;    // shadow 名目(僅紀錄;PnL 走 % 與此無關)

    private async Task SweepVrpShadowAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        if (!_registry.HasAvailableWorker("quote.ohlcv")) return;   // 沒資料源就不跑

        var now = DateTime.UtcNow;

        // ── 1) 結算到期 open 腿 ──────────────────────────────────────
        List<VrpShadowLegEntry> openLegs;
        try
        {
            openLegs = _db.Query<VrpShadowLegEntry>("SELECT * FROM vrp_shadow_legs WHERE settled_at IS NULL");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "VRP shadow: open-legs query failed (likely uninitialized)");
            return;
        }

        foreach (var leg in openLegs)
        {
            if (ct.IsCancellationRequested) return;
            if (!VrpShadowRecorder.ShouldSettle(leg, now)) continue;
            try
            {
                var closes = await VrpFetchRecentDailyClosesAsync(leg.Currency, VrpHorizonDays + 5, ct);
                if (closes.Count < 2) continue;   // 資料不足、下個 cycle 再試(不誤結算成 0)
                // roll 視窗 ≈ 最近 horizon+1 根日 close(結算發生在 roll_end 當下)
                var window = closes.Count > VrpHorizonDays + 1
                    ? closes.GetRange(closes.Count - (VrpHorizonDays + 1), VrpHorizonDays + 1)
                    : closes;
                var realizedVol = VrpShadowMath.RealizedVolAnnualizedPct(window);
                VrpShadowRecorder.ApplySettlement(leg, realizedVol, now);
                _db.Update(leg);
                _logger.LogInformation(
                    "[VRP-SHADOW] settled {Id}: implied={Dvol:0.0} realized={RV:0.0} → naked={Naked:0.0}% capped={Capped:0.0}%",
                    leg.Id, leg.DvolEntry, realizedVol, leg.PnlPctNaked, leg.PnlPctCapped);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VRP shadow: settle failed for {Id}", leg.Id);
            }
        }

        // ── 2) roll 邊界開新腿 ──────────────────────────────────────
        try
        {
            var lastLeg = _db.Query<VrpShadowLegEntry>(
                "SELECT * FROM vrp_shadow_legs ORDER BY entry_time DESC LIMIT 1").FirstOrDefault();
            if (!VrpShadowRecorder.ShouldOpenNewLeg(now, lastLeg, VrpHorizonDays)) return;

            var dvol = await VrpFetchLatestDvolAsync(VrpCurrency, ct);
            if (dvol is null or <= 0m) return;   // 沒 DVOL(Phase 0 未部署/未回補)→ 靜默 skip、不亂開
            var spot = await VrpFetchLatestSpotAsync(VrpCurrency, ct) ?? 0m;

            var leg = VrpShadowRecorder.BuildNewLeg(
                VrpCurrency, now, dvol.Value, spot, VrpSizeScalar, VrpHorizonDays, VrpSleeveNotional, VrpCapPct);
            try { _db.Insert(leg); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "VRP shadow: insert {Id} skipped (likely dup)", leg.Id);
                return;
            }
            _logger.LogInformation("[VRP-SHADOW] opened {Id}: dvol={Dvol:0.0} spot={Spot} roll_end={End:yyyy-MM-dd}",
                leg.Id, dvol.Value, spot, leg.RollEndTime);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VRP shadow: open pass failed");
        }
    }

    /// <summary>讀最新 Deribit DVOL(quote.ohlcv get_dvol、回最後一筆值;無資料回 null)。</summary>
    private async Task<decimal?> VrpFetchLatestDvolAsync(string currency, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { symbol = currency, limit = 5 });
        var res = await _dispatcher.DispatchAsync(BuildRequest("quote.ohlcv", "get_dvol", payload));
        if (!res.Success) return null;
        var root = JsonDocument.Parse(res.ResultPayload ?? "{}").RootElement;
        if (!root.TryGetProperty("dvol", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return null;
        var last = arr[arr.GetArrayLength() - 1];
        return last.TryGetProperty("dvol", out var v) && v.TryGetDecimal(out var d) ? d : null;
    }

    /// <summary>讀最新標的 spot(最後一根日 close)。</summary>
    private async Task<decimal?> VrpFetchLatestSpotAsync(string currency, CancellationToken ct)
    {
        var closes = await VrpFetchRecentDailyClosesAsync(currency, 2, ct);
        return closes.Count > 0 ? closes[^1] : null;
    }

    /// <summary>讀近 N 根日 close(quote.ohlcv get_bars、舊→新順序;失敗回空)。</summary>
    private async Task<List<decimal>> VrpFetchRecentDailyClosesAsync(string currency, int limit, CancellationToken ct)
    {
        var outl = new List<decimal>();
        var payload = JsonSerializer.Serialize(new { symbol = currency, interval = "1d", limit });
        var res = await _dispatcher.DispatchAsync(BuildRequest("quote.ohlcv", "get_bars", payload));
        if (!res.Success) return outl;
        var root = JsonDocument.Parse(res.ResultPayload ?? "{}").RootElement;
        if (!root.TryGetProperty("bars", out var bars) || bars.ValueKind != JsonValueKind.Array) return outl;
        foreach (var b in bars.EnumerateArray())
        {
            if (b.TryGetProperty("close", out var c) && c.TryGetDecimal(out var close) && close > 0)
                outl.Add(close);
        }
        return outl;
    }
}
