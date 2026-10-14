namespace Broker.NyaChat;

public interface INyaModelRouter
{
        NyaLlmProfile Resolve(string task, NyaChatConfig config);
}

public sealed class StaticTaskRouter : INyaModelRouter
{
    private readonly ILogger<StaticTaskRouter> _logger;
    private readonly HashSet<string> _warnedTasks = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _warnLock = new();

    public StaticTaskRouter(ILogger<StaticTaskRouter> logger) => _logger = logger;

    public NyaLlmProfile Resolve(string task, NyaChatConfig config)
    {
        var profileName = ResolveProfileName(task, config);

        if (config.LlmProfiles is { Count: > 0 } &&
            config.LlmProfiles.TryGetValue(profileName, out var profile) && profile != null)
            return profile;

        return Synthesize(task, config);
    }

    private static string ResolveProfileName(string task, NyaChatConfig config)
    {
        if (config.TaskRouting != null &&
            config.TaskRouting.TryGetValue(task, out var name) && !string.IsNullOrWhiteSpace(name))
            return name;

        return task switch
        {
            NyaLlmTasks.Chat           => "chat_default",
            NyaLlmTasks.Summarization  => "summarization",
            _                          => task
        };
    }

        private NyaLlmProfile Synthesize(string task, NyaChatConfig config)
    {
        switch (task)
        {
            case NyaLlmTasks.Summarization:
            {
                var provider = string.IsNullOrWhiteSpace(config.SummarizationProvider)
                    ? config.ChatProvider : config.SummarizationProvider;
                WarnIfTransportMismatch(task, provider, config);
                return new NyaLlmProfile
                {
                    Provider       = provider,
                    BaseUrl        = config.ChatBaseUrl,
                    ApiKey         = config.ChatApiKey,
                    Model          = string.IsNullOrWhiteSpace(config.SummarizationModel)
                        ? config.ChatModel : config.SummarizationModel,
                    Temperature    = 0.3f,
                    TopP           = 0.9f,
                    TimeoutSeconds = config.SummarizationTimeoutSeconds
                };
            }

            default:
                return new NyaLlmProfile
                {
                    Provider       = config.ChatProvider,
                    BaseUrl        = config.ChatBaseUrl,
                    ApiKey         = config.ChatApiKey,
                    Model          = config.ChatModel,
                    Temperature    = config.Temperature,
                    TopP           = config.TopP,
                    TimeoutSeconds = config.ChatTimeoutSeconds
                };
        }
    }

        private void WarnIfTransportMismatch(string task, string taskProvider, NyaChatConfig config)
    {
        if (string.Equals(taskProvider, config.ChatProvider, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_warnLock)
        {
            if (!_warnedTasks.Add(task)) return;
        }
        _logger.LogWarning(
            "[NyaModelRouter] Legacy synthesis for task '{Task}': provider '{TaskProvider}' differs from chat provider " +
            "'{ChatProvider}' but reuses ChatBaseUrl/ChatApiKey (the known coupling bug). Migrate to NyaChat:LlmProfiles " +
            "so this task carries its own BaseUrl/ApiKey.",
            task, taskProvider, config.ChatProvider);
    }
}
