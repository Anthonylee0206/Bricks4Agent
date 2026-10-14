namespace Broker.NyaChat;

public sealed record NyaLlmProviderTemplate(
    string Id, string Label, string Provider, string DefaultBaseUrl, Dictionary<string, double> DefaultParams, string DefaultModel = "", System.Text.Json.Nodes.JsonObject? ExtraBody = null);

public static class NyaLlmProviderTemplates
{
    public static readonly IReadOnlyList<NyaLlmProviderTemplate> All = new[]
    {
        new NyaLlmProviderTemplate("deepseek", "DeepSeek", "openai_chat", "https://api.deepseek.com/chat/completions",
            new() { ["top_p"] = 1.0 }, "deepseek-flash", System.Text.Json.Nodes.JsonNode.Parse("{\"thinking\":{\"type\":\"disabled\"}}")!.AsObject()),
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
