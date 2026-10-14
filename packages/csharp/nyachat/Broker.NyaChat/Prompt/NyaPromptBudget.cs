namespace Broker.NyaChat;

public sealed class NyaPromptBudget
{
    public int InputTokenBudget { get; init; } = int.MaxValue;
    public double NoteRatio { get; init; } = 0.40;
    public double SummaryRatio { get; init; } = 0.35;

        public static NyaPromptBudget Unlimited => new() { InputTokenBudget = int.MaxValue };

    public bool IsUnlimited => InputTokenBudget == int.MaxValue;
}

public sealed class NyaPromptBudgetReport
{
    public object? Selection { get; set; }
    public int EstimatedTokens { get; set; }
    public int NotesIncluded { get; set; }
    public int NotesDropped { get; set; }
    public int SummariesIncluded { get; set; }
    public int SummariesDropped { get; set; }
    public int HistoryIncluded { get; set; }
    public int HistoryDropped { get; set; }

        public bool MandatoryOverflow { get; set; }

    public bool Truncated => NotesDropped + SummariesDropped + HistoryDropped > 0;
}
