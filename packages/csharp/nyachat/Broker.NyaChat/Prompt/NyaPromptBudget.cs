namespace Broker.NyaChat;

/// <summary>
/// System prompt 的 token 預算輸入。
/// </summary>
/// <remarks>
/// <see cref="InputTokenBudget"/> 為「整個 prompt（system + 歷史 + 當前訊息）可用的 token 總額」，
/// 由 Orchestrator 以 <c>MaxContextTokens − 輸出保留額 − 安全緩衝</c> 算出。
/// <see cref="FactRatio"/> / <see cref="SummaryRatio"/> 為扣除固定段與必留事實後，
/// 剩餘預算中 facts / summaries 的上限占比；history 取其餘。
/// </remarks>
public sealed class NyaPromptBudget
{
    public int InputTokenBudget { get; init; } = int.MaxValue;
    public double FactRatio { get; init; } = 0.40;
    public double SummaryRatio { get; init; } = 0.35;

    /// <summary>不限制（經典預設路徑，不套用 Token 預算，供相容路徑與測試使用）。</summary>
    public static NyaPromptBudget Unlimited => new() { InputTokenBudget = int.MaxValue };

    public bool IsUnlimited => InputTokenBudget == int.MaxValue;
}

/// <summary>
/// System prompt 預算填充的結果報告。供 Orchestrator 審計與可觀測。
/// </summary>
public sealed class NyaPromptBudgetReport
{
    public int EstimatedTokens { get; set; }
    public int FactsIncluded { get; set; }
    public int FactsDropped { get; set; }
    public int SummariesIncluded { get; set; }
    public int SummariesDropped { get; set; }
    public int HistoryIncluded { get; set; }
    public int HistoryDropped { get; set; }

    /// <summary>必留事實（instruction/identity）合計即已超出預算（極端情況，記 warning 用）。</summary>
    public bool MandatoryOverflow { get; set; }

    public bool Truncated => FactsDropped + SummariesDropped + HistoryDropped > 0;
}
