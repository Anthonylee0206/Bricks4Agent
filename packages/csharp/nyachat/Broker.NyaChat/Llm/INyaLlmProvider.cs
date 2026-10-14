namespace Broker.NyaChat;

public static class NyaLlmTasks
{
    public const string Chat           = "chat";
    public const string Summarization  = "summarization";
}

public interface INyaLlmProvider
{
        string Key { get; }

        bool SupportsTools => true;

        Task<NyaLlmResponse?> SendAsync(NyaLlmProviderRequest request, CancellationToken ct);
}

public sealed class NyaLlmProviderRequest
{
        public required NyaLlmProfile Profile { get; init; }

        public required NyaLlmRequest Request { get; init; }
}
