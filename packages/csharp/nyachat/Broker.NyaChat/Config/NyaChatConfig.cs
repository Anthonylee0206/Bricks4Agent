namespace Broker.NyaChat;

public class NyaChatConfig
{
    public int NoteCapacity { get; set; } = 32;
    public int NoteMaxLength { get; set; } = 512;
    public int TopicCapacity { get; set; } = 64;

    public bool Enabled { get; set; } = true;

    public string DbPath { get; set; } = "nyachat.db";

    public int ShortTermMessageLimit { get; set; } = 200;

    public bool AutoSummarizeEnabled { get; set; } = false;

    public int SummarizeTriggerCount { get; set; } = 12;

    public int SummarizeKeepRecentCount { get; set; } = 6;

    public int SummarizeMaxBatchSize { get; set; } = 8;

    public int SummarizeMaxOutputTokens { get; set; } = 2048;

    public int SummaryVersionRetention { get; set; } = 5;

    public Dictionary<string, NyaLlmProfile> LlmProfiles { get; set; } = new();

    public Dictionary<string, string> TaskRouting { get; set; } = new();

    public string ChatProvider { get; set; } = "ollama";
    public string ChatBaseUrl { get; set; } = "http://localhost:11434";
    public string ChatApiKey { get; set; } = "";
    public string ChatModel { get; set; } = "qwen3.6:35b-a3b-Q4";
    public float Temperature { get; set; } = 0.7f;
    public float TopP { get; set; } = 0.9f;
    public int ChatTimeoutSeconds { get; set; } = 120;

    public string SummarizationProvider { get; set; } = "";
    public string SummarizationModel { get; set; } = "";
    public int SummarizationTimeoutSeconds { get; set; } = 90;

    public int DefaultMaxContextTokens { get; set; } = 32768;

    public double OutputReserveRatio { get; set; } = 0.25;

    public int MinOutputTokens { get; set; } = 256;

    public int MaxOutputTokens { get; set; } = 2048;

    public int ContextSafetyMargin { get; set; } = 256;

    public double SummaryTokenBudgetRatio { get; set; } = 0.35;

    public int PerToolResultMaxTokens { get; set; } = 1024;

    public int MaxToolRounds { get; set; } = 3;

    public int ToolExecTimeoutSeconds { get; set; } = 30;

    public double TokenEstimatorCjkPerChar { get; set; } = 1.5;

    public double TokenEstimatorLatinPerChar { get; set; } = 0.25;

    public int MaxReplyLength { get; set; } = 2000;

    public bool StripMarkdown { get; set; } = true;

    public bool EnableMultiPartReply { get; set; } = true;

    public bool RagEnabled { get; set; } = false;

    public string DefaultSoulId { get; set; } = "default";

    public string SoulsDirectory { get; set; } = "souls";

    public string PromptsDirectory { get; set; } = "prompts";

    public bool AuditEnabled { get; set; } = true;
}
