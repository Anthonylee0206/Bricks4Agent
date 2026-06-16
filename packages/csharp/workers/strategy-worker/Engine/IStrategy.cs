using StrategyWorker.Models;

namespace StrategyWorker.Engine;

/// <summary>
/// 策略介面。接收 K 線資料 + 設定、產出訊號。
///
/// 新增 metadata 用 default impl、既有 13 個策略不需動就拿到合理 fallback；
/// 想精細控制（例如 RsiStrategy 知道自己只要 14 bar、grid search 不要當 50 bar）
/// 就 override 對應屬性。Registry / Lab / dashboard 都從這拿單一事實來源。
/// </summary>
public interface IStrategy
{
    string Name { get; }
    Signal Evaluate(List<BarData> bars, StrategyConfig config);

    /// <summary>給人讀的說明、/strategy/list / dashboard tooltip 用。</summary>
    string Description => "(策略沒寫描述)";

    /// <summary>分類—— trend follow、mean reversion、breakout 等。dashboard 過濾用。</summary>
    StrategyCategory Category => StrategyCategory.Other;

    /// <summary>最少需要幾根 bar 才能算出有效訊號（少於這個數量、Evaluate 應該回 hold）。</summary>
    int MinBars => 50;

    /// <summary>策略 deploy 的最低有效資金（USDT）——Lab recommendations 拿來打「too-small-capital」tag。</summary>
    decimal MinCapitalUsdt => 100m;

    /// <summary>
    /// 參數 schema。key = 參數名（snake_case）、value = 範圍與預設值。
    /// 給 grid search 知道掃哪幾個維度、給 dashboard 知道 user 能調哪些。
    /// 沒 override 就回空 dictionary、Lab 落入「跑 default 不掃參」分支。
    /// </summary>
    IReadOnlyDictionary<string, ParamSpec> ParamSchema => EmptyParamSchema;

    private static readonly IReadOnlyDictionary<string, ParamSpec> EmptyParamSchema
        = new Dictionary<string, ParamSpec>();
}

public enum StrategyCategory
{
    Trend,           // sma_cross, vegas_tunnel, super_trend, parabolic_sar
    MeanReversion,   // rsi_oversold, bollinger_bands, cci, keltner, mfi
    Breakout,        // donchian
    Momentum,        // macd_divergence, rsi_stoch
    Pattern,         // harmonic_pattern, fibonacci_retracement, price_action
    MultiTimeframe,  // multi_timeframe
    Composite,       // composite, ensemble, auto_select
    Sentiment,       // llm, news_sentiment
    Volume,          // obv, chaikin_mf — 量價類獨立、便於 dashboard 過濾
    Other,
}

/// <summary>
/// 單一參數的 schema。型別 + 範圍給 grid search、Description 給 dashboard tooltip。
/// </summary>
public sealed class ParamSpec
{
    public string Type { get; init; } = "int";          // "int" / "decimal" / "string"
    public object Default { get; init; } = 0;
    public object? Min { get; init; }
    public object? Max { get; init; }
    public object? Step { get; init; }
    public string? Description { get; init; }
    /// <summary>顯式列出可選 values（例如 strategy="composite" 的 vote_threshold 可能只接 [0.5, 0.66, 0.75]）。</summary>
    public object[]? Choices { get; init; }
}

/// <summary>
/// 簡化的 K 線資料（從 broker 查詢結果反序列化）。
/// 避免直接依賴 quote-worker 的 OhlcvBar。
/// </summary>
public class BarData
{
    public DateTime OpenTime { get; set; }
    public decimal Open   { get; set; }
    public decimal High   { get; set; }
    public decimal Low    { get; set; }
    public decimal Close  { get; set; }
    public decimal Volume { get; set; }
    public decimal? FundingRate  { get; set; }   // 永續資金費率(perp 才有;非 perp/無資料 = null)
    public decimal? OpenInterest { get; set; }   // 未平倉量(perp 才有;非 perp/無資料 = null)
    public decimal? RetailLongShortRatio { get; set; }   // 散戶多空比(>1 = 散戶看多;Q2 2026-05-28 OOS 驗證真 alpha 源 t=-2.89/-2.25)
    public decimal? BtcRet { get; set; }   // 當日 BTC 報酬(strat-validate 注入;BTC-lead alt-lag 策略用、跨幣領先訊號)
    public decimal? TakerLsRatio { get; set; }   // taker 主動買/賣量比(order-flow;>1=主動買壓主導;data.binance.vision metrics)
    public decimal? CotSpecNet { get; set; }   // COT 投機者(non-commercial)淨持倉 % OI(結構性部位;極端=擁擠;strat-validate 從 CFTC 注入)
    public decimal? DvolValue { get; set; }   // Deribit DVOL 隱含波動指數(年化 %;VRP/波動 carry 用;隱含>實現=賣方溢酬;quote-worker AlignDvol 注入)
}
