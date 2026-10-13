using Broker.NyaChat.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Broker.NyaChat;

/// <summary>
/// NyaChat 的相依注入（DI）註冊封裝。
/// broker 端只需 <c>AddNyaChat(config)</c> + 視需要 <c>AddNyaTool&lt;T&gt;()</c>，
/// 取代原本散落在 Program.cs 的 11 個 AddSingleton 與命名 HttpClient。
/// </summary>
public static class NyaChatServiceCollectionExtensions
{
    /// <summary>
    /// 註冊 NyaChat 所有核心服務。封裝內容與遷移前 Program.cs 的註冊<b>逐一對應、行為一致</b>。
    /// </summary>
    public static IServiceCollection AddNyaChat(this IServiceCollection services, NyaChatConfig config)
    {
        services.AddSingleton(config);                                                   // seed：供 ConfigStore 啟動載入

        // 資料隔離（2026-06-13）：AI 產出進 NyaChat 專屬 SQLite（config.DbPath），不寫宿主 broker.db。
        // 三個 DB 服務經 factory 餵 NyaDb.Db——建構式簽章不變，直接建構的測試零改動。
        services.AddSingleton<NyaDb>();
        services.AddSingleton<INyaConfigStore<NyaChatConfig>>(sp => new NyaChatConfigStore(
            sp.GetRequiredService<NyaChatConfig>(),
            sp.GetRequiredService<NyaDb>().Db,
            sp.GetRequiredService<ILogger<NyaChatConfigStore>>()));                       // 註冊可重載配置快照管理服務
        services.AddHttpClient("nya-llm");
        services.AddSingleton(sp => new NyaAuditLogger(
            sp.GetRequiredService<NyaDb>().Db,
            sp.GetRequiredService<INyaConfigStore<NyaChatConfig>>(),
            sp.GetRequiredService<ILogger<NyaAuditLogger>>()));
        services.AddSingleton(sp => new NyaMemoryService(
            sp.GetRequiredService<NyaDb>().Db,
            sp.GetRequiredService<INyaConfigStore<NyaChatConfig>>(),
            sp.GetRequiredService<ILogger<NyaMemoryService>>()));
        services.AddSingleton<NyaSoulProvider>();
        // Prompt 模板提供者（JSON 外部化 + 熱重載 + fallback）。須在消費者
        // （PromptBuilder / FactExtractor / Summarizer）之前註冊。
        services.AddSingleton<NyaPromptTemplateProvider>();
        services.AddSingleton<NyaPromptBuilder>();

        // Doc 3 Plan A：LLM profile/route 專店為唯一真相。
        // StaticTaskRouter 已不再被 NyaLlmClient 消費，僅供首次 legacy→store 遷移用（見 NyaLlmProfileStore.MigrateFromLegacyIfEmpty）。
        services.TryAddSingleton<INyaModelRouter, StaticTaskRouter>();
        services.AddNyaLlmProvider<OllamaProvider>();
        services.AddNyaLlmProvider<OpenAiChatProvider>();
        services.AddNyaLlmProvider<OpenAiResponsesProvider>();
        services.AddNyaLlmProvider<AnthropicProvider>();   // Doc 3 Plan B：claude 家族
        services.AddNyaLlmProvider<GeminiProvider>();      // Doc 3 Plan B：gemini 家族
        services.AddSingleton(sp =>
        {
            var store = new NyaLlmProfileStore(
                sp.GetRequiredService<NyaDb>().Db,
                sp.GetRequiredService<ILogger<NyaLlmProfileStore>>(),
                sp.GetService<INyaSecretProtector>());
            // 啟動時若店空，用 seed config + router 一次性遷移（冪等）。
            store.MigrateFromLegacyIfEmpty(
                sp.GetRequiredService<NyaChatConfig>(),
                sp.GetRequiredService<INyaModelRouter>());
            return store;
        });
        services.AddSingleton(sp => new NyaLlmClient(
            sp.GetRequiredService<NyaLlmProfileStore>(),
            sp.GetServices<INyaLlmProvider>(),
            sp.GetRequiredService<ILogger<NyaLlmClient>>()));

        services.AddSingleton<NyaReplyPostProcessor>();
        services.AddSingleton<NyaFactExtractor>();
        services.AddSingleton<NyaSummarizer>();

        // Token 估算器。比率為啟動期固定的啟發式旋鈕（由 seed 配置帶入）；
        // 預算數値（MaxContextTokens / ratio / 上限）走快照熱重載，由 Orchestrator 讀當前快照。
        services.AddSingleton(new NyaTokenEstimator(config.TokenEstimatorCjkPerChar, config.TokenEstimatorLatinPerChar));

        // 字面指令攔截擴充點，預設無作用；broker/discord 可後續以 AddSingleton 覆蓋（最後註冊者勝）。
        services.TryAddSingleton<INyaPreChatCommandHandler, NullNyaPreChatCommandHandler>();

        services.AddSingleton<NyaChatOrchestrator>();

        // 註冊工具系統相依項目（啟停 Store、工具登錄器等）。
        services.AddSingleton<NyaToolEnableStore>();
        services.AddSingleton<NyaToolRegistry>();
        services.AddNyaTool<FactToolPlugin>();                       // 內建：upsert_fact / delete_fact
        services.AddNyaTool<TopicToolPlugin>();                      // 內建話題操作插件：new_topic / switch_topic / rename_topic
        // Nya 內建 fallback：一律允許。broker 在 Program.cs 以 BrokerToolAuthorizer 覆蓋（最後註冊者勝）。
        services.AddSingleton<INyaToolAuthorizer, AllowAllToolAuthorizer>();

        // guest gate fallback（2026-07-08）：未接 broker 時一律放行（單元測試 / 非 broker 宿主行為不變）。
        // broker 在 Program.cs 以 BrokerPrincipalResolver 覆蓋；TryAdd 讓測試可先註冊 fake。
        services.TryAddSingleton<INyaPrincipalResolver, AllowAllPrincipalResolver>();

        // 對外唯一對話入口介面綁定。停用時綁 Null 物件，
        // 使 broker 在 NyaChat 停用或缺件時仍能啟動，消費端可走既有 fallback。
        if (config.Enabled)
            services.AddSingleton<INyaChatOrchestrator>(sp => sp.GetRequiredService<NyaChatOrchestrator>());
        else
            services.AddSingleton<INyaChatOrchestrator, NullNyaChatOrchestrator>();

        return services;
    }

    /// <summary>
    /// 註冊一個工具插件。「加工具 = 加一個類別 + 一行註冊，零 Nya 核心改動」。
    /// </summary>
    public static IServiceCollection AddNyaTool<T>(this IServiceCollection services)
        where T : class, INyaToolPlugin
    {
        services.AddSingleton<INyaToolPlugin, T>();
        return services;
    }

    /// <summary>
    /// 註冊一個 LLM 提供者。「加 provider = 一個類別 + 一行」，與 <see cref="AddNyaTool{T}"/> 對稱。
    /// 由 <see cref="NyaLlmClient"/> 依 profile 的 <c>Provider</c> 鍵選用；三個內建 provider 已在 <see cref="AddNyaChat"/> 掛上。
    /// </summary>
    public static IServiceCollection AddNyaLlmProvider<T>(this IServiceCollection services)
        where T : class, INyaLlmProvider
    {
        services.AddSingleton<INyaLlmProvider, T>();
        return services;
    }
}

/// <summary>guest gate 的 Nya 內建 fallback：一律視為正式使用者。</summary>
internal sealed class AllowAllPrincipalResolver : INyaPrincipalResolver
{
    public bool IsAuthorized(string userId, string channelType) => true;
}
