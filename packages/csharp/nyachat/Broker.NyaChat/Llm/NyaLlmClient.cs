using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

public class NyaLlmClient
{
    private readonly NyaLlmProfileStore _store;
    private readonly IReadOnlyDictionary<string, INyaLlmProvider> _providers;
    private readonly ILogger<NyaLlmClient> _logger;
    private readonly NyaAuditLogger? _auditLogger;

    public NyaLlmClient(
        NyaLlmProfileStore store,
        IEnumerable<INyaLlmProvider> providers,
        ILogger<NyaLlmClient> logger, NyaAuditLogger? auditLogger = null)
    {
        _store = store;
        _logger = logger;
        _auditLogger = auditLogger;

        var map = new Dictionary<string, INyaLlmProvider>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in providers)
        {
            if (string.IsNullOrWhiteSpace(p.Key))
                throw new InvalidOperationException($"LLM provider {p.GetType().Name} has empty Key.");
            if (map.ContainsKey(p.Key))
                throw new InvalidOperationException(
                    $"Duplicate LLM provider key '{p.Key}' from {p.GetType().Name} " +
                    $"(already provided by {map[p.Key].GetType().Name}).");
            map[p.Key] = p;
        }
        if (map.Count == 0)
            throw new InvalidOperationException("No INyaLlmProvider registered. Call AddNyaChat / AddNyaLlmProvider<T>().");
        _providers = map;

        var chatProfile = _store.ResolveProfile(NyaLlmTasks.Chat);
        var chatKey = (chatProfile.Provider ?? "ollama").Trim().ToLowerInvariant();
        if (map.TryGetValue(chatKey, out var chatProvider) && !chatProvider.SupportsTools)
            throw new InvalidOperationException(
                $"Chat task is routed to provider '{chatKey}' ({chatProvider.GetType().Name}) which does not " +
                $"support tools (SupportsTools=false). Tool calling (function-calling closed loop) would be " +
                $"silently disabled — unacceptable now that governed quant tools are mounted. " +
                $"Route the chat task to a tools-capable provider via NyaChat:LlmProfiles / TaskRouting.");

        _logger.LogInformation("[NyaLlmClient] Registered providers: {Keys}", string.Join(", ", map.Keys));
    }

        public Task<NyaLlmResponse?> ChatAsync(NyaLlmRequest request, CancellationToken ct = default)
        => DispatchAsync(NyaLlmTasks.Chat, request, ct);

        public async Task<string?> SummarizeAsync(NyaLlmRequest request, CancellationToken ct = default)
    {
        request.Tools = null;
        var resp = await DispatchAsync(NyaLlmTasks.Summarization, request, ct);
        return resp?.Content;
    }

        public NyaLlmProfile ResolveProfile(string callSiteId) => _store.ResolveProfile(callSiteId);

    private Task<NyaLlmResponse?> DispatchAsync(string task, NyaLlmRequest request, CancellationToken ct)
    {
        var profile = _store.ResolveProfile(task);

        request.Model       = profile.Model;
        request.Temperature = profile.Temperature;
        request.TopP        = profile.TopP;

        var key = (profile.Provider ?? "ollama").Trim().ToLowerInvariant();
        if (!_providers.TryGetValue(key, out var provider))
        {

            provider = _providers.TryGetValue("openai_chat", out var fallback)
                ? fallback : _providers.Values.First();
            _logger.LogWarning(
                "[NyaLlmClient] Unknown provider '{Provider}' for task '{Task}'; falling back to '{Fallback}'.",
                profile.Provider, task, provider.Key);
        }

        if (request.Tools is { Count: > 0 } && !provider.SupportsTools)
        {
            _logger.LogWarning(
                "[NyaLlmClient] Task '{Task}' resolved to provider '{Provider}' which does not support tools; " +
                "{Count} tool(s) will be silently ignored (closed-loop disabled). Check TaskRouting/profile.",
                task, provider.Key, request.Tools.Count);
        }

        if (!string.IsNullOrWhiteSpace(request.AuditUserId) && !string.IsNullOrWhiteSpace(request.AuditTraceId))
            _auditLogger?.LogLlmRequest(request.AuditUserId, request.AuditTraceId, task, provider.Key, profile, request);
        return provider.SendAsync(new NyaLlmProviderRequest { Profile = profile, Request = request }, ct);
    }
}
