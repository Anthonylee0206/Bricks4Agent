namespace Broker.Services;

/// <summary>
/// VRP / 波動 carry shadow 的純量測函式(無 I/O、無 env、無時間 → 純輸入→純輸出)。
/// 釘死「shadow 怎麼算 PnL」:賣方收隱含波動、賠實現波動,淨 = 隱含 − 實現(vega 慣例)。
///
/// 這是 textbook 變異數 carry 的**代理基準**(非 alpha):精緻 condor strike 選擇 / premium 模型 /
/// sizing 曲線(∝DVOL cap 1.0)屬 alpha、留私有 strategy-worker;本檔只做誰都會的 vega P&L + 封頂。
/// 同時提供裸賣(無封頂、研究的「不可部署幻覺」)與封頂(defined-risk、可部署)兩條 → shadow 自證封頂有效。
/// 見 docs/designs/vrp-shadow-deploy-sketch.md §3.3。
/// </summary>
public static class VrpShadowMath
{
    /// <summary>
    /// 從日收盤序列算年化實現波動(%)。close-to-close 對數報酬的樣本標準差(n-1)× √365 × 100。
    /// 有效報酬 < 2 筆 → 0(資料不足、recorder 應 skip 該結算)。非正收盤跳過(防 log 爆)。
    /// </summary>
    public static decimal RealizedVolAnnualizedPct(IReadOnlyList<decimal> closes)
    {
        if (closes == null || closes.Count < 2) return 0m;

        var rets = new List<double>(closes.Count - 1);
        for (int i = 1; i < closes.Count; i++)
        {
            if (closes[i] <= 0 || closes[i - 1] <= 0) continue;
            rets.Add(Math.Log((double)(closes[i] / closes[i - 1])));
        }
        if (rets.Count < 2) return 0m;

        double mean = rets.Average();
        double sumSq = 0;
        foreach (var r in rets) sumSq += (r - mean) * (r - mean);
        double variance = sumSq / (rets.Count - 1);            // 樣本變異數(n-1)
        double annualized = Math.Sqrt(variance) * Math.Sqrt(365.0) * 100.0;
        return (decimal)annualized;
    }

    /// <summary>
    /// 裸賣變異數 PnL %(vega 慣例):sizeScalar × (隱含 − 實現)波動點。
    /// sizeScalar = 每 1 vol-point 對應的權益 %(∝DVOL、cap 1.0 的 sizing 在 recorder 決定、本函式只吃結果)。
    /// 實現 ≫ 隱含 → 大負(研究的「裸賣不可部署」左尾、無封頂)。
    /// </summary>
    public static decimal PnlNakedPct(decimal dvolEntryPct, decimal realizedVolPct, decimal sizeScalar)
        => sizeScalar * (dvolEntryPct - realizedVolPct);

    /// <summary>
    /// defined-risk(condor)封頂後 PnL %:下檔虧損封到 −|capPct|(可部署值)。
    /// 上檔自然受 premium 上限約束、此代理不額外封上檔(裸賣上檔本就 ≤ 收到的隱含)。
    /// </summary>
    public static decimal PnlCappedPct(decimal pnlNakedPct, decimal capPct)
    {
        var floor = -Math.Abs(capPct);
        return pnlNakedPct < floor ? floor : pnlNakedPct;
    }
}
