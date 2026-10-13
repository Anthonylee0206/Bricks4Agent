namespace Broker.NyaChat;

/// <summary>
/// 任務 → LLM profile 的解析。
/// </summary>
/// <remarks>
/// 本項<b>只提供靜態映射</b>（<see cref="StaticTaskRouter"/>）。動態策略（按用戶等級 / 對話複雜度 /
/// 負載切換）需要一個目前不存在的「路由決策輸入」，過早實作＝過度設計，故僅以本介面當<b>可選注入的
/// 擴展點</b>：未來要做策略時換實作即可，broker 以 <c>AddSingleton&lt;INyaModelRouter, …&gt;()</c> 覆蓋。
/// </remarks>
public interface INyaModelRouter
{
    /// <summary>解析某任務當前生效的 profile。找不到具名 profile 時由 legacy 扁平欄位合成（向後相容）。</summary>
    NyaLlmProfile Resolve(string task, NyaChatConfig config);
}

/// <summary>
/// 靜態任務路由。<c>TaskRouting[task] → LlmProfiles[name]</c>；
/// 缺失時由 <see cref="NyaChatConfig"/> 的 legacy 扁平欄位<b>合成等價 profile</b>，
/// 確保既有 appsettings 一字不改即可運作。
/// </summary>
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

        // 找不到具名 profile（或完全未設定 LlmProfiles）→ 由 legacy 扁平欄位合成（向後相容）
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
            NyaLlmTasks.FactExtraction => "extraction",
            NyaLlmTasks.Summarization  => "summarization",
            _                          => task
        };
    }

    /// <summary>
    /// 由 legacy 扁平欄位合成 profile。chat 直接對映；fact_extraction / summarization 沿用
    /// 「空=繼承 Chat 模型」的舊語意，並<b>保留</b>各任務原本的低溫取樣（抽取 0.2 / 摘要 0.3）。
    /// </summary>
    private NyaLlmProfile Synthesize(string task, NyaChatConfig config)
    {
        switch (task)
        {
            case NyaLlmTasks.FactExtraction:
            {
                var provider = string.IsNullOrWhiteSpace(config.FactExtractionProvider)
                    ? config.ChatProvider : config.FactExtractionProvider;
                WarnIfTransportMismatch(task, provider, config);
                return new NyaLlmProfile
                {
                    Provider       = provider,
                    BaseUrl        = config.ChatBaseUrl,   // legacy 沿用 Chat transport（這正是被根除的 bug 來源）
                    ApiKey         = config.ChatApiKey,
                    Model          = string.IsNullOrWhiteSpace(config.FactExtractionModel)
                        ? config.ChatModel : config.FactExtractionModel,
                    Temperature    = 0.2f,                 // 抽取低溫（原 NyaFactExtractor 設值）
                    TopP           = 0.9f,
                    TimeoutSeconds = config.FactExtractionTimeoutSeconds
                };
            }

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
                    Temperature    = 0.3f,                 // 摘要低溫（原 NyaSummarizer 設值）
                    TopP           = 0.9f,
                    TimeoutSeconds = config.SummarizationTimeoutSeconds
                };
            }

            default: // chat
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

    /// <summary>
    /// legacy 合成時，若任務 provider 與 Chat provider 不同卻仍沿用 Chat 的 BaseUrl/ApiKey，
    /// 記一次性 warning 提示遷移到 <c>LlmProfiles</c>（改用 profiles 後此耦合自動消失）。
    /// </summary>
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
