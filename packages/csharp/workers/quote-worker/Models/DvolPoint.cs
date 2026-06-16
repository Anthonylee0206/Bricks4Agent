namespace QuoteWorker.Models;

/// <summary>
/// Deribit DVOL(BTC/ETH 隱含波動指數,類 VIX,annualized vol points)一筆紀錄。
/// 2026-06-16 VRP / 波動 carry shadow 資料源(見 docs/designs/vrp-shadow-deploy-sketch.md)。
/// 跟 funding / retail_ls / open_interest 路徑平行,QuoteOhlcvHandler 對齊後 emit dvol。
/// Deribit public REST 免金鑰:GET /api/v2/public/get_volatility_index_data。
/// </summary>
public class DvolPoint
{
    public string Symbol { get; set; } = string.Empty;   // Deribit currency:"BTC" / "ETH"
    public DateTime SampleTime { get; set; }
    public decimal DvolValue { get; set; }   // 隱含波動指數(年化 vol points,例 55.0 = 55%)
}
