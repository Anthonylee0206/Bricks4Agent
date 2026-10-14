namespace Broker.NyaChat.Abstractions;

public sealed class NyaToolContext
{
    public string Scope { get; init; } = "normal";
    public int? NoteTokenBudget { get; init; }
    public Func<string, int>? EstimateTokens { get; init; }
    public string? ExternalConversationId { get; init; }
    public IReadOnlySet<string> CurrentContextMessageIds { get; init; } = new HashSet<string>();

    public string ToolName { get; init; } = "";

    public string UserId { get; init; } = "";

    public string ChannelType { get; init; } = "";

    public string? ConversationId { get; init; }

    public string TraceId { get; init; } = "";

    public string ArgumentsJson { get; init; } = "{}";
}
