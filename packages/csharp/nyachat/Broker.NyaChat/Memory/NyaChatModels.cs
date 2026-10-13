using BaseOrm;

namespace Broker.NyaChat;

// ─── nya_messages ─────────────────────────────────────────────────────────────
/// <summary>
/// 短期記憶：逐則對話紀錄（append-only，支援軟刪除與摘要標記）
/// is_summarized=true 的訊息不會被喂進 LLM，但保留可追溯性
/// </summary>
[Table("nya_messages")]
public class NyaMessage
{
    [Key(AutoIncrement = false)]
    [Column("message_id")]
    public string MessageId { get; set; } = "";

    [Column("user_id")]
    public string UserId { get; set; } = "";

    /// <summary>"user" | "assistant" | "system"</summary>
    [Column("role")]
    public string Role { get; set; } = "";

    [Column("content")]
    public string Content { get; set; } = "";

    /// <summary>全域遞增序號，用於排序與摘要範圍界定</summary>
    [Column("sequence")]
    public long Sequence { get; set; }

    /// <summary>話題分段 ID（由外部手動 API 設定，不自動切段）</summary>
    [Column("topic_id")]
    public string? TopicId { get; set; }

    /// <summary>軟刪除旗標</summary>
    [Column("is_deleted")]
    public bool IsDeleted { get; set; }

    /// <summary>已被長期摘要涵蓋，不再送入 LLM context</summary>
    [Column("is_summarized")]
    public bool IsSummarized { get; set; }

    /// <summary>額外 metadata（model, latency_ms, token_count 等）</summary>
    [Column("metadata_json")]
    public string MetadataJson { get; set; } = "{}";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ─── nya_facts ────────────────────────────────────────────────────────────────
/// <summary>
/// 結構化事實：LLM 從對話中抽取的使用者偏好、身份、明確指令。
/// 每個 (user_id, fact_key) 最多保留 5 個版本；超過時刪除最舊版。
/// is_active=true 代表當前生效版本。
/// </summary>
[Table("nya_facts")]
public class NyaFact
{
    [Key(AutoIncrement = false)]
    [Column("fact_id")]
    public string FactId { get; set; } = "";

    [Column("user_id")]
    public string UserId { get; set; } = "";

    /// <summary>"identity" | "preference" | "context" | "instruction"</summary>
    [Column("category")]
    public string Category { get; set; } = "";

    /// <summary>e.g. "name", "language", "job_title", "soul_id"</summary>
    [Column("fact_key")]
    public string FactKey { get; set; } = "";

    /// <summary>事實內容（純事實文字，不是摘要）</summary>
    [Column("fact_value")]
    public string FactValue { get; set; } = "";

    /// <summary>LLM 抽取信心分數 0.0–1.0</summary>
    [Column("confidence")]
    public float Confidence { get; set; } = 1.0f;

    /// <summary>來源訊息 ID（可追溯回原始對話）</summary>
    [Column("source_message_id")]
    public string? SourceMessageId { get; set; }

    [Column("version")]
    public int Version { get; set; } = 1;

    /// <summary>是否為當前生效版本</summary>
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>被新版本覆蓋的時間（null = 仍在生效）</summary>
    [Column("superseded_at")]
    public DateTime? SupersededAt { get; set; }
}

// ─── nya_summaries ────────────────────────────────────────────────────────────
/// <summary>
/// 長期摘要：對被標記為 is_summarized 的對話段落所生成的精華摘要。
/// 每個 user 最多保留 5 個版本（相同涵蓋範圍的重新摘要）。
/// </summary>
[Table("nya_summaries")]
public class NyaSummary
{
    [Key(AutoIncrement = false)]
    [Column("summary_id")]
    public string SummaryId { get; set; } = "";

    [Column("user_id")]
    public string UserId { get; set; } = "";

    /// <summary>所屬話題 ID（摘要依話題獨立）。null = 舊版/全域摘要（向後相容）。</summary>
    [Column("topic_id")]
    public string? TopicId { get; set; }

    [Column("summary_text")]
    public string SummaryText { get; set; } = "";

    /// <summary>涵蓋的訊息序號範圍起點（對應 nya_messages.sequence）</summary>
    [Column("covered_from_seq")]
    public long CoveredFromSeq { get; set; }

    /// <summary>涵蓋的訊息序號範圍終點</summary>
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

// ─── nya_topics ───────────────────────────────────────────────────────────────
/// <summary>
/// 持久化話題：每個 user 有一個 active 話題，只有明確操作才切換。
/// 對話記錄的 topic_id 欄位即引用此表的 topic_id。
/// </summary>
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

/// <summary>話題列表查詢結果（含訊息計數）</summary>
public class NyaTopicEntry
{
    [Column("topic_id")]   public string TopicId    { get; set; } = "";
    [Column("user_id")]    public string UserId     { get; set; } = "";
    [Column("title")]      public string? Title     { get; set; }
    [Column("is_active")]  public bool   IsActive   { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("msg_count")]  public int    MsgCount   { get; set; }
}

// ─── nya_audit_log ────────────────────────────────────────────────────────────
/// <summary>
/// 操作審計日誌：所有引擎決策、記憶變更、LLM 呼叫都在此留下紀錄。
/// append-only，不刪除，供追蹤與除錯。
/// </summary>
[Table("nya_audit_log")]
public class NyaAuditLog
{
    [Key(AutoIncrement = false)]
    [Column("log_id")]
    public string LogId { get; set; } = "";

    [Column("user_id")]
    public string UserId { get; set; } = "";

    /// <summary>
    /// "chat_input"      — 使用者訊息進入，記錄記憶 context 統計<br/>
    /// "chat_output"     — LLM 最終回覆，記錄延遲與分段數<br/>
    /// "tool_call"       — LLM 決定呼叫工具，記錄 tool name + arguments<br/>
    /// "tool_result"     — tool 執行完成，記錄結果（success/error/skipped）<br/>
    /// "fact_extract"    — 事實抽取完成（或略過）<br/>
    /// "fact_upsert"     — 事實寫入或更新<br/>
    /// "fact_rollback"   — 事實版本回滾<br/>
    /// "summary_create"  — 長期摘要建立<br/>
    /// "summary_rollback"— 摘要版本回滾<br/>
    /// "memory_delete"   — 記憶刪除（fact / message / all）<br/>
    /// "topic_create"    — 話題建立<br/>
    /// "topic_switch"    — 話題切換<br/>
    /// "topic_rename"    — 話題重新命名<br/>
    /// "tool_toggle"     — 工具啟用 / 停用（系統操作）<br/>
    /// "error"           — 任何流程中的例外錯誤
    /// </summary>
    [Column("action")]
    public string Action { get; set; } = "";

    /// <summary>Phase 4-A：一句話→工具→審批 全鏈路關聯 ID（同一對話輪共用；非 turn 項為空）。</summary>
    [Column("trace_id")]
    public string TraceId { get; set; } = "";

    /// <summary>結構化細節（JSON）</summary>
    [Column("detail_json")]
    public string DetailJson { get; set; } = "{}";

    /// <summary>"success" | "error" | "skipped"</summary>
    [Column("result")]
    public string Result { get; set; } = "success";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// ─── Value Objects ─────────────────────────────────────────────────────────────
/// <summary>LLM 事實抽取的單筆結果</summary>
public class FactExtractionResult
{
    public string Category { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public float Confidence { get; set; } = 1.0f;
}

/// <summary>NyaChat 引擎的對話回傳結果</summary>
public class NyaChatResult
{
    /// <summary>可能因長度切割而分多段</summary>
    public List<string> Replies { get; set; } = new();

    public string? Error { get; set; }

    /// <summary>目前使用者的有效訊息數（未刪除、未摘要）</summary>
    public int HistoryCount { get; set; }

    /// <summary>當前話題 ID（供 LINE 通知使用）</summary>
    public string? TopicId { get; set; }
}

/// <summary>LLM 訊息格式（統一格式，由各 Provider 翻譯為原生格式）</summary>
public class NyaLlmMessage
{
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    /// <summary>Tool result message 的 tool call ID（role=tool 時使用）</summary>
    public string? ToolCallId { get; set; }
    /// <summary>Assistant 訊息中的 tool calls（role=assistant 且有 tool 呼叫時）</summary>
    public List<NyaToolCall>? ToolCalls { get; set; }
    /// <summary>Tool result 的函式名稱（role=tool 時使用）</summary>
    public string? Name { get; set; }
}

/// <summary>LLM 回應（含文字內容或工具呼叫）</summary>
public class NyaLlmResponse
{
    public string? Content { get; set; }
    public List<NyaToolCall>? ToolCalls { get; set; }
    public bool HasToolCalls => ToolCalls?.Count > 0;
}

/// <summary>LLM 呼叫工具的單筆記錄</summary>
public class NyaToolCall
{
    public string Id { get; set; } = "";
    public string FunctionName { get; set; } = "";
    public string FunctionArguments { get; set; } = "{}";
}

/// <summary>傳給 LLM 的工具定義</summary>
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
    public string Type { get; set; } = "object";
    public Dictionary<string, NyaLlmParameterProperty> Properties { get; set; } = new();
    public List<string> Required { get; set; } = new();
}

public class NyaLlmParameterProperty
{
    public string Type { get; set; } = "string";
    public string? Enum { get; set; }
    public string Description { get; set; } = "";
}

/// <summary>統一 LLM 請求格式</summary>
public class NyaLlmRequest
{
    public string Model { get; set; } = "";
    public List<NyaLlmMessage> Messages { get; set; } = new();
    public float Temperature { get; set; } = 0.7f;
    public float TopP { get; set; } = 0.9f;
    public int? MaxTokens { get; set; }
    public List<NyaLlmTool>? Tools { get; set; }
}

// ─── Admin API 回應 DTO ────────────────────────────────────────────────────────

/// <summary>使用者列表單筆摘要（GET /nya/users 回傳）</summary>
public class NyaUserListEntry
{
    [Column("user_id")]   public string UserId { get; set; } = "";
    [Column("msg_count")] public int MsgCount { get; set; }
    [Column("last_at")]   public DateTime? LastAt { get; set; }
    // 由 ListUsers 在記憶體中填入，不來自 SQL 直接映射
    [Ignore] public int ActiveFactCount { get; set; }
    [Ignore] public string SoulId { get; set; } = "default";
}

/// <summary>使用者全局總覽（GET /nya/users/{userId}/overview 回傳）</summary>
public class NyaUserOverview
{
    public string UserId { get; set; } = "";
    public string SoulId { get; set; } = "default";
    public NyaMsgStats Messages { get; set; } = new();
    public NyaFactStats Facts { get; set; } = new();
    public NyaSummaryStats Summaries { get; set; } = new();
    public DateTime? LastMessageAt { get; set; }
    public DateTime? LastFactUpdatedAt { get; set; }
}

public class NyaMsgStats
{
    public int Total { get; set; }       // 含已刪除的全部
    public int Active { get; set; }      // 未刪除且未摘要
    public int Summarized { get; set; }
    public int Deleted { get; set; }
}

public class NyaFactStats
{
    public int TotalActive { get; set; }
    public Dictionary<string, int> Categories { get; set; } = new();
}

public class NyaSummaryStats
{
    /// <summary>所有摘要列（含歷史版本，跨話題）。</summary>
    public int TotalVersions { get; set; }

    /// <summary>有 active 摘要的話題數（per-topic 隔離下，每話題各一筆 active）。</summary>
    public int ActiveSummaryCount { get; set; }

    /// <summary>所有 active 摘要中，涵蓋到的最大訊息序號。</summary>
    public long LatestCoveredToSeq { get; set; }
}

/// <summary>摘要嘗試的結果（供 trigger 端點顯示精確原因；背景路徑忽略）。</summary>
public enum NyaSummarizeStatus
{
    Created,            // 成功建立新摘要
    NotEnoughMessages, // 可壓縮訊息不足（保留最近後沒有更早的）
    LlmEmpty,          // LLM 回空內容（如 thinking 耗盡輸出預算）
    Error              // 例外
}

/// <summary>摘要嘗試結果（Status + 可選 SummaryId / 原因）。</summary>
public readonly record struct NyaSummarizeOutcome(
    NyaSummarizeStatus Status,
    string? SummaryId = null,
    string? Reason = null)
{
    public bool Created => Status == NyaSummarizeStatus.Created;
}

/// <summary>GROUP BY 事實計數行（ListUsers 內部用）</summary>
public class NyaUserFactCountRow
{
    [Column("user_id")]    public string UserId { get; set; } = "";
    [Column("fact_count")] public int FactCount { get; set; }
}

/// <summary>Soul 綁定查詢行（ListUsers 內部用）</summary>
public class NyaUserSoulRow
{
    [Column("user_id")]  public string UserId { get; set; } = "";
    [Column("soul_val")] public string SoulVal { get; set; } = "default";
}

// ─── nya_soul_bindings ────────────────────────────────────────────────────────
/// <summary>
/// 使用者 Soul 綁定：每位使用者指定一個 Soul ID。
/// 獨立於事實系統，無版本控制，由管理者操作。
/// </summary>
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

/// <summary>Soul 人格定義（從 JSON 檔案載入）</summary>
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
