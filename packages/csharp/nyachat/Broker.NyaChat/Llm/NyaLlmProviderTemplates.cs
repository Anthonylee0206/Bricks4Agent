namespace Broker.NyaChat;

/// <summary>供應商家族模板（Doc 3 Plan C §6）：建 profile 時預填 provider / base_url / 預設參數。</summary>
public sealed record NyaLlmProviderTemplate(
    string Id, string Label, string Provider, string DefaultBaseUrl, Dictionary<string, double> DefaultParams);

public static class NyaLlmProviderTemplates
{
    public static readonly IReadOnlyList<NyaLlmProviderTemplate> All = new[]
    {
        new NyaLlmProviderTemplate("chatgpt", "ChatGPT (OpenAI)", "openai_chat", "https://api.openai.com/v1/chat/completions",
            new() { ["temperature"] = 0.7, ["top_p"] = 0.9 }),
        new NyaLlmProviderTemplate("local", "本地 Ollama", "ollama", "http://localhost:11434/api/chat",
            new() { ["temperature"] = 0.7, ["top_p"] = 0.9 }),
        new NyaLlmProviderTemplate("lmstudio", "本地 LM Studio", "openai_chat", "http://localhost:1234/v1/chat/completions",
            new() { ["temperature"] = 0.7, ["top_p"] = 0.9 }),
        new NyaLlmProviderTemplate("claude", "Claude (Anthropic)", "anthropic", "https://api.anthropic.com/v1/messages",
            new() { ["temperature"] = 0.7, ["top_p"] = 0.9 }),
        new NyaLlmProviderTemplate("gemini", "Gemini (Google)", "gemini", "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent",
            new() { ["temperature"] = 0.7, ["top_p"] = 0.9 }),
    };
}
