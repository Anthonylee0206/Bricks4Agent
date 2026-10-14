namespace Broker.NyaChat;

public sealed class NyaLlmProfile
{
    public string ProfileId { get; set; } = "";
        public string Provider { get; set; } = "ollama";

    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";

    public System.Text.Json.Nodes.JsonObject? ExtraBody { get; set; }
    public bool SendTemperature { get; set; } = true;

    public float Temperature { get; set; } = 0.7f;
    public float TopP { get; set; } = 0.9f;
    public int TimeoutSeconds { get; set; } = 120;

        public int? MaxContextTokens { get; set; }

        public Dictionary<string, double>? ModelOverrides { get; set; }

        public bool? EnableThinking { get; set; }

        public int? ThinkingBudget { get; set; }
}
