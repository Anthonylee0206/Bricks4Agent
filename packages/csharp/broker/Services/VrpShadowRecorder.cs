using BrokerCore.Models;

namespace Broker.Services;

/// <summary>
/// VRP / 波動 carry shadow recorder 的純生命週期決策(無 I/O、無 env、無時間參數以外的時間)。
/// 「何時開新 roll 腿 / 何時結算 / 開腿與結算各填什麼」釘死成可測純函式;
/// 真正讀 DVOL/bars + 寫 DB + 掛 AutoTrader sweep 的 impure 薄層呼叫這些(下一增量)。
///
/// always-on(不擇時、研究已驗門檻擇時過擬合)、roll 邊界 = 每 HorizonDays 開一條。
/// sizeScalar(∝DVOL cap1.0)與 condor strike/premium = alpha、由 impure 層 / 私有決定後傳入,本檔不含。
/// 見 docs/designs/vrp-shadow-deploy-sketch.md §3.3。
/// </summary>
public static class VrpShadowRecorder
{
    /// <summary>到 roll 邊界該開新腿嗎:沒有任何腿、或上一條腿已滿 horizon(該換 roll)。always-on。</summary>
    public static bool ShouldOpenNewLeg(DateTime nowUtc, VrpShadowLegEntry? lastLeg, int horizonDays)
    {
        if (lastLeg == null) return true;
        return nowUtc >= lastLeg.EntryTime.AddDays(horizonDays);
    }

    /// <summary>
    /// 建一條新 shadow 腿(進場輸入快照;結算欄位留空到 ApplySettlement)。
    /// 冪等 Id = "vrp:{currency}:{entry_ms}" → 同一 roll 邊界不重開。
    /// </summary>
    public static VrpShadowLegEntry BuildNewLeg(
        string currency, DateTime entryUtc, decimal dvolEntry, decimal spotEntry,
        decimal sizeScalar, int horizonDays, decimal notional, decimal capPct,
        string structure = "iron_condor", string ownerPrincipalId = "prn_dashboard")
    {
        long entryMs = new DateTimeOffset(entryUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return new VrpShadowLegEntry
        {
            Id               = $"vrp:{currency}:{entryMs}",
            Currency         = currency,
            EntryTime        = entryUtc,
            RollEndTime      = entryUtc.AddDays(horizonDays),
            HorizonDays      = horizonDays,
            DvolEntry        = dvolEntry,
            SpotEntry        = spotEntry,
            SizeScalar       = sizeScalar,
            Notional         = notional,
            Structure        = structure,
            CapPct           = capPct,
            Shadow           = true,
            OwnerPrincipalId = ownerPrincipalId,
            CreatedAt        = entryUtc,
            UpdatedAt        = entryUtc,
        };
    }

    /// <summary>open 腿到期該結算嗎:還沒 settled 且 now ≥ roll_end。</summary>
    public static bool ShouldSettle(VrpShadowLegEntry leg, DateTime nowUtc)
        => leg.SettledAt == null && nowUtc >= leg.RollEndTime;

    /// <summary>
    /// 用視窗實現波動結算:算裸/封頂雙 PnL、填 settled。就地改 leg 並回傳(便於測試與鏈式)。
    /// realizedVolPct 由 impure 層從標的視窗 OHLCV 經 [[VrpShadowMath.RealizedVolAnnualizedPct]] 算好傳入。
    /// </summary>
    public static VrpShadowLegEntry ApplySettlement(VrpShadowLegEntry leg, decimal realizedVolPct, DateTime settledUtc)
    {
        leg.RealizedVol    = realizedVolPct;
        leg.PnlPctNaked    = VrpShadowMath.PnlNakedPct(leg.DvolEntry, realizedVolPct, leg.SizeScalar);
        leg.PnlPctCapped   = VrpShadowMath.PnlCappedPct(leg.PnlPctNaked, leg.CapPct);
        leg.SettledAt      = settledUtc;
        leg.CloseReason    = "settled";
        leg.UpdatedAt      = settledUtc;
        return leg;
    }
}
