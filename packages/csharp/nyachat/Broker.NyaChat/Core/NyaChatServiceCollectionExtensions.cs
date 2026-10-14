using Broker.NyaChat.Abstractions;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Broker.NyaChat;

public static class NyaChatServiceCollectionExtensions
{

    public static IServiceCollection AddNyaChat(this IServiceCollection services, NyaChatConfig config)
    {
        services.AddSingleton(config);

        services.AddSingleton<NyaDb>();
        services.AddSingleton<INyaConfigStore<NyaChatConfig>>(sp => new NyaChatConfigStore(
            sp.GetRequiredService<NyaChatConfig>(),
            sp.GetRequiredService<NyaDb>().Db,
            sp.GetRequiredService<ILogger<NyaChatConfigStore>>()));
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
        services.AddHostedService<NyaMemoryStartup>();

        services.AddSingleton<NyaPromptTemplateProvider>();
        services.AddSingleton<NyaPromptBuilder>();

        services.TryAddSingleton<INyaModelRouter, StaticTaskRouter>();
        services.AddNyaLlmProvider<OllamaProvider>();
        services.AddNyaLlmProvider<OpenAiChatProvider>();
        services.AddNyaLlmProvider<OpenAiResponsesProvider>();
        services.AddNyaLlmProvider<AnthropicProvider>();
        services.AddNyaLlmProvider<GeminiProvider>();
        services.AddSingleton(sp =>
        {
            var store = new NyaLlmProfileStore(
                sp.GetRequiredService<NyaDb>().Db,
                sp.GetRequiredService<ILogger<NyaLlmProfileStore>>(),
                sp.GetService<INyaSecretProtector>());

            store.MigrateFromLegacyIfEmpty(
                sp.GetRequiredService<NyaChatConfig>(),
                sp.GetRequiredService<INyaModelRouter>());
            return store;
        });
        services.AddSingleton(sp => new NyaLlmClient(
            sp.GetRequiredService<NyaLlmProfileStore>(),
            sp.GetServices<INyaLlmProvider>(),
            sp.GetRequiredService<ILogger<NyaLlmClient>>(),
            sp.GetRequiredService<NyaAuditLogger>()));

        services.AddSingleton<NyaReplyPostProcessor>();
        services.AddSingleton<NyaSummarizer>();

        services.AddSingleton(new NyaTokenEstimator(config.TokenEstimatorCjkPerChar, config.TokenEstimatorLatinPerChar));

        services.TryAddSingleton<INyaPreChatCommandHandler, NullNyaPreChatCommandHandler>();

        services.AddSingleton<NyaChatOrchestrator>();

        services.AddSingleton<NyaToolEnableStore>();
        services.AddSingleton<NyaToolRegistry>();
        services.AddSingleton<FactToolPlugin>();
        services.AddSingleton<INyaToolPlugin>(sp => sp.GetRequiredService<FactToolPlugin>());
        services.AddNyaTool<HistorySearchToolPlugin>();
        services.AddNyaTool<TopicToolPlugin>();

        services.AddSingleton<INyaToolAuthorizer, AllowAllToolAuthorizer>();

        if (config.Enabled)
            services.AddSingleton<INyaChatOrchestrator>(sp => sp.GetRequiredService<NyaChatOrchestrator>());
        else
            services.AddSingleton<INyaChatOrchestrator, NullNyaChatOrchestrator>();

        return services;
    }

    public static IServiceCollection AddNyaTool<T>(this IServiceCollection services)
        where T : class, INyaToolPlugin
    {
        services.AddSingleton<INyaToolPlugin, T>();
        return services;
    }

    public static IServiceCollection AddNyaLlmProvider<T>(this IServiceCollection services)
        where T : class, INyaLlmProvider
    {
        services.AddSingleton<INyaLlmProvider, T>();
        return services;
    }
}
