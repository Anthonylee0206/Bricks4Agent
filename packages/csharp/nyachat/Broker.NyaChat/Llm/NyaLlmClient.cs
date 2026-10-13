using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// LLM facade。退化為 thin facade：<b>解析 profile（經 <see cref="NyaLlmProfileStore"/>，
/// 依 call_site_id → route → profile）→ 依 profile.Provider 選 <see cref="INyaLlmProvider"/> → 委派</b>。
/// HTTP 建構 / 回應解析 / 模型覆寫全部下放到各 provider 與 <see cref="NyaLlmSerialization"/>。
/// </summary>
/// <remarks>
/// <para>
/// 公開 API（<see cref="ChatAsync"/> / <see cref="ExtractAsync"/> / <see cref="SummarizeAsync"/>）維持不變，
/// 消費端（Orchestrator / FactExtractor / Summarizer）零簽章改動。
/// </para>
/// <para>
/// 取樣參數（Temperature/TopP）的<b>單一事實來源是 profile</b>：facade 以 profile 值對齊 request，
/// 各任務的低溫（抽取 0.2 / 摘要 0.3）由其 profile 承載（appsettings 或 legacy 合成）。
/// <c>MaxTokens</c> 屬 per-request 輸出上限，維持由呼叫端設定。
/// </para>
/// </remarks>
public class NyaLlmClient
{
    private readonly NyaLlmProfileStore _store;
    private readonly IReadOnlyDictionary<string, INyaLlmProvider> _providers;
    private readonly ILogger<NyaLlmClient> _logger;

    public NyaLlmClient(
        NyaLlmProfileStore store,
        IEnumerable<INyaLlmProvider> providers,
        ILogger<NyaLlmClient> logger)
    {
        _store = store;
        _logger = logger;

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

        // Phase 4-C 債2：啟動期 fail-fast——chat 是唯一會帶 tools 的任務（extract/summarize 明確 Tools=null）。
        // 若 chat profile 路由到不支援 tools 的 provider，工具閉環會在執行期靜默失效（量化工具掛上後 =
        // 金融功能靜默故障）。從「執行期 warning」升級為「啟動期拒絕」，讓設定錯誤無法上線。
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

    // ── 公開方法（簽章不變）──────────────────────────────────────────────────

    /// <summary>使用對話 profile 發送請求，支援 function calling，回傳結構化回應。</summary>
    public Task<NyaLlmResponse?> ChatAsync(NyaLlmRequest request, CancellationToken ct = default)
        => DispatchAsync(NyaLlmTasks.Chat, request, ct);

    /// <summary>使用事實抽取 profile（不使用 tools，直接回傳文字）。</summary>
    public async Task<string?> ExtractAsync(NyaLlmRequest request, CancellationToken ct = default)
    {
        request.Tools = null; // 抽取任務不使用 tools
        var resp = await DispatchAsync(NyaLlmTasks.FactExtraction, request, ct);
        return resp?.Content;
    }

    /// <summary>使用摘要 profile（不使用 tools，直接回傳文字）。</summary>
    public async Task<string?> SummarizeAsync(NyaLlmRequest request, CancellationToken ct = default)
    {
        request.Tools = null; // 摘要任務不使用 tools
        var resp = await DispatchAsync(NyaLlmTasks.Summarization, request, ct);
        return resp?.Content;
    }

    /// <summary>解析某任務當前生效的 profile（供消費端做 audit / metadata 記錄實際 model/provider）。</summary>
    public NyaLlmProfile ResolveProfile(string callSiteId) => _store.ResolveProfile(callSiteId);

    // ── 分派 ──────────────────────────────────────────────────────────────────

    private Task<NyaLlmResponse?> DispatchAsync(string task, NyaLlmRequest request, CancellationToken ct)
    {
        var profile = _store.ResolveProfile(task);

        // profile 為模型與取樣的權威來源（單一事實來源）
        request.Model       = profile.Model;
        request.Temperature = profile.Temperature;
        request.TopP        = profile.TopP;

        var key = (profile.Provider ?? "ollama").Trim().ToLowerInvariant();
        if (!_providers.TryGetValue(key, out var provider))
        {
            // 維持原本「default = openai_chat」語意
            provider = _providers.TryGetValue("openai_chat", out var fallback)
                ? fallback : _providers.Values.First();
            _logger.LogWarning(
                "[NyaLlmClient] Unknown provider '{Provider}' for task '{Task}'; falling back to '{Fallback}'.",
                profile.Provider, task, provider.Key);
        }

        // 防呆（QC P5）：帶 tools 的請求被路由到不支援工具的 provider，工具會被該 provider 靜默忽略。
        // Phase 4-C 債2 後，chat 任務在建構式已 fail-fast，此處對 chat 已是不可達的防禦；
        // 仍保留以兜「非 chat 任務（extract/summarize）意外帶 tools」的理論情況（目前該兩任務固定 Tools=null）。
        if (request.Tools is { Count: > 0 } && !provider.SupportsTools)
        {
            _logger.LogWarning(
                "[NyaLlmClient] Task '{Task}' resolved to provider '{Provider}' which does not support tools; " +
                "{Count} tool(s) will be silently ignored (closed-loop disabled). Check TaskRouting/profile.",
                task, provider.Key, request.Tools.Count);
        }

        return provider.SendAsync(new NyaLlmProviderRequest { Profile = profile, Request = request }, ct);
    }
}
