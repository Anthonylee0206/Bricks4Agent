using BaseOrm;

namespace Broker.NyaChat;

[Table("nya_turns")]
public sealed class NyaTurn
{
    [Key(AutoIncrement = false), Column("turn_id")] public string TurnId { get; set; } = "";
    [Column("user_id")] public string UserId { get; set; } = "";
    [Column("scope")] public string Scope { get; set; } = "normal";
    [Column("conversation_id")] public string? ConversationId { get; set; }
    [Column("start_topic_id")] public string StartTopicId { get; set; } = "";
    [Column("current_topic_id")] public string CurrentTopicId { get; set; } = "";
    [Column("status")] public string Status { get; set; } = "pending";
    [Column("failure_reason")] public string? FailureReason { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("finished_at")] public DateTime? FinishedAt { get; set; }
}

[Table("nya_notes")]
public sealed class NyaNote
{
    [Column("topic_id")] public string? TopicId { get; set; }
    [Key(AutoIncrement = false), Column("version_id")] public string VersionId { get; set; } = "";
    [Column("note_id")] public string NoteId { get; set; } = "";
    [Column("user_id")] public string UserId { get; set; } = "";
    [Column("scope")] public string Scope { get; set; } = "normal";
    [Column("conversation_id")] public string? ConversationId { get; set; }
    [Column("note_key")] public string Key { get; set; } = "";
    [Column("content")] public string Content { get; set; } = "";
    [Column("version")] public int Version { get; set; }
    [Column("is_current")] public bool IsCurrent { get; set; }
    [Column("expires_at")] public DateTime? ExpiresAt { get; set; }
    [Column("source_message_ids")] public string SourceMessageIdsJson { get; set; } = "[]";
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("nya_hide_refs")]
public sealed class NyaHideReference
{
    [Key(AutoIncrement = false), Column("reference_id")] public string ReferenceId { get; set; } = "";
    [Column("forget_id")] public string ForgetId { get; set; } = "";
    [Column("user_id")] public string UserId { get; set; } = "";
    [Column("scope")] public string Scope { get; set; } = "normal";
    [Column("conversation_id")] public string? ConversationId { get; set; }
    [Column("kind")] public string Kind { get; set; } = "";
    [Column("target_id")] public string TargetId { get; set; } = "";
    [Column("is_active")] public bool IsActive { get; set; } = true;
    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("nya_memory_selections")]
public sealed class NyaMemorySelection
{
    [Key(AutoIncrement = false), Column("selection_id")] public string SelectionId { get; set; } = "";
    [Column("user_id")] public string UserId { get; set; } = "";
    [Column("scope")] public string Scope { get; set; } = "normal";
    [Column("conversation_id")] public string? ConversationId { get; set; }
    [Column("visibility_version")] public long VisibilityVersion { get; set; }
    [Column("note_ids")] public string NoteIdsJson { get; set; } = "[]";
    [Column("message_ids")] public string MessageIdsJson { get; set; } = "[]";
    [Column("expires_at")] public DateTime ExpiresAt { get; set; }
}

[Table("nya_history_cursors")]
public sealed class NyaHistoryCursor
{
    [Key(AutoIncrement = false), Column("cursor_id")] public string CursorId { get; set; } = "";
    [Column("user_id")] public string UserId { get; set; } = "";
    [Column("scope")] public string Scope { get; set; } = "normal";
    [Column("conversation_id")] public string? ConversationId { get; set; }
    [Column("query_key")] public string QueryKey { get; set; } = "";
    [Column("visibility_version")] public long VisibilityVersion { get; set; }
    [Column("ids")] public string IdsJson { get; set; } = "[]";
    [Column("offset")] public int Offset { get; set; }
    [Column("expires_at")] public DateTime ExpiresAt { get; set; }
}

[Table("nya_turn_work")]
public sealed class NyaTurnWork
{
    [Key(AutoIncrement = false), Column("work_id")] public string WorkId { get; set; } = "";
    [Column("turn_id")] public string TurnId { get; set; } = "";
    [Column("position")] public int Position { get; set; }
    [Column("call_json")] public string CallJson { get; set; } = "{}";
    [Column("result_json")] public string ResultJson { get; set; } = "{}";
    [Column("effects_json")] public string EffectsJson { get; set; } = "[]";
    [Column("dependencies")] public string DependenciesJson { get; set; } = "[]";
    [Column("is_hidden")] public bool IsHidden { get; set; }
}

public sealed class NyaMemoryException(string code, string field, string message) : Exception(message)
{
    public string Code { get; } = code;
    public string Field { get; } = field;
}

public class NyaHistoryMessage : NyaMessage
{
    [BaseOrm.Column("turn_status")] public string? TurnStatus { get; set; }
}
