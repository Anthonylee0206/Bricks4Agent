using BaseOrm;

namespace Broker.NyaChat;

[Table("nya_messages")]
public class NyaMessage
{
    [Column("turn_id")] public string TurnId { get; set; } = "";
    [Column("origin_topic_id")] public string? OriginTopicId { get; set; }
    [Column("scope")] public string Scope { get; set; } = "normal";
    [Column("conversation_id")] public string? ConversationId { get; set; }
    [Key(AutoIncrement = false)]
    [Column("message_id")]
    public string MessageId { get; set; } = "";

    [Column("user_id")]
    public string UserId { get; set; } = "";

    [Column("role")]
    public string Role { get; set; } = "";

    [Column("content")]
    public string Content { get; set; } = "";

    [Column("sequence")]
    public long Sequence { get; set; }

    [Column("topic_id")]
    public string? TopicId { get; set; }

    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    [Column("is_summarized")]
    public bool IsSummarized { get; set; }

    [Column("metadata_json")]
    public string MetadataJson { get; set; } = "{}";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("nya_summaries")]
public class NyaSummary
{
    [Column("source_message_ids")] public string SourceMessageIdsJson { get; set; } = "[]";
    [Column("visibility_version")] public long VisibilityVersion { get; set; }
    [Column("scope")] public string Scope { get; set; } = "normal";
    [Column("conversation_id")] public string? ConversationId { get; set; }
    [Key(AutoIncrement = false)]
    [Column("summary_id")]
    public string SummaryId { get; set; } = "";

    [Column("user_id")]
    public string UserId { get; set; } = "";

    [Column("topic_id")]
    public string? TopicId { get; set; }

    [Column("summary_text")]
    public string SummaryText { get; set; } = "";

    [Column("covered_from_seq")]
    public long CoveredFromSeq { get; set; }

    [Column("covered_to_seq")]
    public long CoveredToSeq { get; set; }

    [Column("message_count")]
    public int MessageCount { get; set; }

    [Column("version")]
    public int Version { get; set; } = 1;

    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("superseded_at")]
    public DateTime? SupersededAt { get; set; }
}

[Table("nya_topics")]
public class NyaTopic
{
    [Key(AutoIncrement = false)]
    [Column("topic_id")]   public string TopicId    { get; set; } = "";
    [Column("user_id")]    public string UserId     { get; set; } = "";
    [Column("title")]      public string? Title     { get; set; }
    [Column("is_active")]  public bool   IsActive   { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

public class NyaTopicEntry
{
    [Column("topic_id")]   public string TopicId    { get; set; } = "";
    [Column("user_id")]    public string UserId     { get; set; } = "";
    [Column("title")]      public string? Title     { get; set; }
    [Column("is_active")]  public bool   IsActive   { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("msg_count")]  public int    MsgCount   { get; set; }
}

[Table("nya_audit_log")]
public class NyaAuditLog
{
    [Key(AutoIncrement = false)]
    [Column("log_id")]
    public string LogId { get; set; } = "";

    [Column("user_id")]
    public string UserId { get; set; } = "";

    [Column("action")]
    public string Action { get; set; } = "";

    [Column("trace_id")]
    public string TraceId { get; set; } = "";

    [Column("detail_json")]
    public string DetailJson { get; set; } = "{}";

    [Column("result")]
    public string Result { get; set; } = "success";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class NyaChatResult
{

    public List<string> Replies { get; set; } = new();

    public string? Error { get; set; }

    public int HistoryCount { get; set; }

    public string? TopicId { get; set; }
}

public class NyaLlmMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";

    public string? ToolCallId { get; set; }

    public List<NyaToolCall>? ToolCalls { get; set; }

    public string? Name { get; set; }
}

public class NyaLlmResponse
{
    public string? Content { get; set; }
    public List<NyaToolCall>? ToolCalls { get; set; }
    public bool HasToolCalls => ToolCalls?.Count > 0;
}

public class NyaToolCall
{
    public string Id { get; set; } = "";
    public string FunctionName { get; set; } = "";
    public string FunctionArguments { get; set; } = "{}";
}

public class NyaLlmTool
{
    public string Type { get; set; } = "function";
    public NyaLlmFunction Function { get; set; } = new();
}

public class NyaLlmFunction
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public NyaLlmFunctionParameters Parameters { get; set; } = new();
}

public class NyaLlmFunctionParameters
{
    public string? RulesJson { get; set; }
    public bool AdditionalProperties { get; set; }
    public string Type { get; set; } = "object";
    public Dictionary<string, NyaLlmParameterProperty> Properties { get; set; } = new();
    public List<string> Required { get; set; } = new();
}

public class NyaLlmParameterProperty
{
    public int? MinLength { get; set; }
    public int? MaxLength { get; set; }
    public double? Minimum { get; set; }
    public double? Maximum { get; set; }
    public int? MinItems { get; set; }
    public int? MaxItems { get; set; }
    public string? Pattern { get; set; }
    public string? Format { get; set; }
    public NyaLlmParameterProperty? Items { get; set; }
    public Dictionary<string, NyaLlmParameterProperty> Properties { get; set; } = new();
    public List<string> Required { get; set; } = new();
    public string? RulesJson { get; set; }
    public string Type { get; set; } = "string";
    public string? Enum { get; set; }
    public string Description { get; set; } = "";
}

public class NyaLlmRequest
{
    public string? AuditTraceId { get; set; }
    public string? AuditUserId { get; set; }
    public string Model { get; set; } = "";
    public List<NyaLlmMessage> Messages { get; set; } = new();
    public float Temperature { get; set; } = 0.7f;
    public float TopP { get; set; } = 0.9f;
    public int? MaxTokens { get; set; }
    public List<NyaLlmTool>? Tools { get; set; }
}

public class NyaUserListEntry
{
    [Column("user_id")]   public string UserId { get; set; } = "";
    [Column("msg_count")] public int MsgCount { get; set; }
    [Column("last_at")]   public DateTime? LastAt { get; set; }

    [Ignore] public int ActiveNoteCount { get; set; }
    [Ignore] public string SoulId { get; set; } = "default";
}

public class NyaUserOverview
{
    public string UserId { get; set; } = "";
    public string SoulId { get; set; } = "default";
    public NyaMsgStats Messages { get; set; } = new();
    public NyaNoteStats Notes { get; set; } = new();
    public NyaSummaryStats Summaries { get; set; } = new();
    public DateTime? LastMessageAt { get; set; }
    public DateTime? LastNoteUpdatedAt { get; set; }
}

public class NyaMsgStats
{
    public int Total { get; set; }
    public int Active { get; set; }
    public int Summarized { get; set; }
    public int Deleted { get; set; }
}

public class NyaNoteStats
{
    public int TotalActive { get; set; }
    public Dictionary<string, int> Categories { get; set; } = new();
}

public class NyaSummaryStats
{

    public int TotalVersions { get; set; }

    public int ActiveSummaryCount { get; set; }

    public long LatestCoveredToSeq { get; set; }
}

public enum NyaSummarizeStatus
{
    Created,
    NotEnoughMessages,
    LlmEmpty,
    Error
}

public readonly record struct NyaSummarizeOutcome(
    NyaSummarizeStatus Status,
    string? SummaryId = null,
    string? Reason = null)
{
    public bool Created => Status == NyaSummarizeStatus.Created;
}

public class NyaUserSoulRow
{
    [Column("user_id")]  public string UserId { get; set; } = "";
    [Column("soul_val")] public string SoulVal { get; set; } = "default";
}

[Table("nya_soul_bindings")]
public class NyaSoulBinding
{
    [Key(AutoIncrement = false)]
    [Column("user_id")]
    public string UserId { get; set; } = "";

    [Column("soul_id")]
    public string SoulId { get; set; } = "";

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class SoulDefinition
{
    public string SoulId { get; set; } = "default";
    public string DisplayName { get; set; } = "NYA";
    public SoulPersonality Personality { get; set; } = new();
    public List<string> Rules { get; set; } = new();
    public List<string> Forbidden { get; set; } = new();
}

public class SoulPersonality
{
    public string Tone { get; set; } = "";
    public string Language { get; set; } = "繁體中文";
    public string GreetingStyle { get; set; } = "";
    public string HumorLevel { get; set; } = "";
    public string Formality { get; set; } = "";
}
