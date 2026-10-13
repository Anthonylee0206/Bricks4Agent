using System.Text.Json;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;

namespace Broker.NyaChat;

/// <summary>
/// 審計日誌服務：所有 NyaChat 引擎的決策、記憶變更、LLM 呼叫都留下可追蹤紀錄。
/// 寫入 nya_audit_log 表（append-only）。
/// </summary>
public class NyaAuditLogger
{
    private readonly BrokerDb _db;
    private readonly INyaConfigStore<NyaChatConfig> _configStore;
    private NyaChatConfig _config => _configStore.Current; // 當前配置快照
    private readonly ILogger<NyaAuditLogger> _logger;

    // Phase 4-C 債3：稽核寫入失敗兜底計數器。Write 的 catch 只記 server log，
    // 稽核 DB 本身壞掉時錯誤會完全遺失、不可觀測。至少留一個程序內計數讓監控/健康檢查抓得到。
    private long _writeFailureCount;
    /// <summary>累積的稽核寫入失敗次數（程序生命週期內）。供健康檢查 / 監控觀測「稽核管線是否在掉資料」。</summary>
    public long WriteFailureCount => System.Threading.Interlocked.Read(ref _writeFailureCount);

    public NyaAuditLogger(BrokerDb db, INyaConfigStore<NyaChatConfig> configStore, ILogger<NyaAuditLogger> logger)
    {
        _db = db;
        _configStore = configStore;
        _logger = logger;
        _db.EnsureTable<NyaAuditLog>();
        // Phase 4-A：加性遷移——舊庫補上 trace_id 欄（不重建表）。
        EnsureColumn("nya_audit_log", "trace_id", "TEXT");
    }

    /// <summary>若指定表缺該欄則 ALTER TABLE 加上（加性、冪等遷移；比照 NyaMemoryService）。</summary>
    private void EnsureColumn(string table, string column, string sqlType)
    {
        try
        {
            var has = _db.Scalar<int>(
                $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @column", new { column });
            if (has == 0) _db.Execute($"ALTER TABLE {table} ADD COLUMN {column} {sqlType}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaAudit] EnsureColumn {Table}.{Column} failed", table, column);
        }
    }

    // ── 寫入 ────────────────────────────────────────────────────────────────

    public void LogChatInput(string userId, string message, string model,
        int factsCount, int summariesCount, int historyCount, int totalLlmMessages,
        int estimatedTokens = 0, int maxContextTokens = 0, bool truncated = false, string? traceId = null)
        => Write(userId, "chat_input", new
        {
            message,
            model,
            context = new
            {
                facts = factsCount,
                summaries = summariesCount,
                history = historyCount,
                total_llm_messages = totalLlmMessages,
                // Token 預算可觀測指標記錄
                estimated_tokens = estimatedTokens,
                max_context_tokens = maxContextTokens,
                truncated
            }
        }, traceId: traceId);

    public void LogChatOutput(string userId, string reply, string model, long latencyMs, int replyCount, string? traceId = null)
        => Write(userId, "chat_output", new
        {
            reply_preview = Truncate(reply, 100),
            model,
            latency_ms = latencyMs,
            part_count = replyCount
        }, traceId: traceId);

    public void LogFactExtraction(string userId, IReadOnlyList<FactExtractionResult> facts, string model,
        int storedCount, int skippedCount)
        => Write(userId, "fact_extract", new
        {
            total = facts.Count,
            stored = storedCount,
            skipped = skippedCount,
            model,
            keys = facts.Select(f => $"{f.Category}/{f.Key}").ToArray()
        });

    public void LogFactExtractionEmpty(string userId, string model, int rawLength, string reason)
        => Write(userId, "fact_extract", new { model, raw_length = rawLength, reason }, "skipped");

    public void LogFactUpsert(string userId, string factKey, string category, int newVersion)
        => Write(userId, "fact_upsert", new { fact_key = factKey, category, new_version = newVersion });

    public void LogFactRollback(string userId, string factKey, int fromVersion, int toVersion)
        => Write(userId, "fact_rollback", new
        {
            fact_key = factKey,
            from_version = fromVersion,
            to_version = toVersion
        });

    public void LogSummaryCreation(string userId, string topicId, string summaryId, long fromSeq, long toSeq, int messageCount, string model)
        => Write(userId, "summary_create", new
        {
            summary_id = summaryId,
            topic_id = topicId,
            from_seq = fromSeq,
            to_seq = toSeq,
            message_count = messageCount,
            model
        });

    /// <summary>
    /// 記錄摘要嘗試未產出（result=skipped）——例如 LLM 回空內容。
    /// 修復：摘要靜默失敗時稽核頁完全沒紀錄，失敗不可觀測。改為一律留痕。
    /// </summary>
    public void LogSummarySkipped(string userId, string topicId, string reason)
        => Write(userId, "summary_create", new { topic_id = topicId, reason }, "skipped");

    public void LogSummaryRollback(string userId, string summaryId, int fromVersion, int toVersion)
        => Write(userId, "summary_rollback", new
        {
            summary_id = summaryId,
            from_version = fromVersion,
            to_version = toVersion
        });

    public void LogMemoryDelete(string userId, string targetType, string? targetId = null)
        => Write(userId, "memory_delete", new { target_type = targetType, target_id = targetId });

    public void LogTopicCreate(string userId, string topicId, string? title = null)
        => Write(userId, "topic_create", new { topic_id = topicId, title });

    public void LogTopicSwitch(string userId, string? fromTopicId, string toTopicId)
        => Write(userId, "topic_switch", new { from_topic_id = fromTopicId, to_topic_id = toTopicId });

    public void LogTopicRename(string userId, string topicId, string title)
        => Write(userId, "topic_rename", new { topic_id = topicId, title });

    /// <summary>guest（無有效身分組）來訊：已落地但不回覆（guest gate，2026-07-08）。</summary>
    public void LogGuestBlocked(string userId, string channelType)
        => Write(userId, "guest_message_blocked", new { channel_type = channelType });

    /// <summary>身分組指派／撤銷（管理端動作）。記在該使用者名下，其稽核分頁可查。action: role_assign | role_revoke。</summary>
    public void LogRoleChange(string userId, string action, string roleId)
        => Write(userId, action, new { role_id = roleId });

    public void LogError(string userId, string action, string error, string? traceId = null)
        => Write(userId, "error", new { original_action = action, error = Truncate(error, 300) }, "error", traceId);

    /// <summary>
    /// 稽核刪除的二級留痕：刪除動作本身寫回稽核表（在刪除之後寫入，故不會被同批刪掉）。
    /// scope: "single" | "user" | "filter" | "all"；filter 為當次刪除套用的篩選條件。修復缺口：先前清空稽核可完全無痕。
    /// </summary>
    public void LogAuditPurge(string scope, int deletedCount, string? target = null, object? filter = null)
        => Write("__sys__", "audit_purge", new { scope, deleted = deletedCount, target, filter });

    public void LogToolToggle(string toolId, bool enabled)
        => Write("__sys__", "tool_toggle", new { tool_id = toolId, enabled });

    public void LogSoulChange(string action, string soulId)
        => Write("__sys__", action, new { soul_id = soulId });

    /// <summary>記錄可重載配置的更新，changedKeys 為本次覆寫的欄位名。</summary>
    public void LogConfigUpdate(IReadOnlyList<string> changedKeys)
        => Write("__sys__", "config_update", new { changed = changedKeys });

    /// <summary>記錄 LLM profile 新增/更新（管理端動作）。絕不記 api_key。</summary>
    public void LogLlmProfileUpsert(string profileId, string provider, string model)
        => Write("__sys__", "llm_profile_upsert", new { profile_id = profileId, provider, model });

    /// <summary>記錄 LLM profile 刪除（管理端動作）。</summary>
    public void LogLlmProfileDelete(string profileId)
        => Write("__sys__", "llm_profile_delete", new { profile_id = profileId });

    /// <summary>記錄 call-site → profile 路由設定（管理端動作）。</summary>
    public void LogLlmRouteSet(string callSiteId, string profileId)
        => Write("__sys__", "llm_route_set", new { call_site_id = callSiteId, profile_id = profileId });

    /// <summary>
    /// 記錄 LLM 決定呼叫 tool 的事件（tool_call）。
    /// toolType: "fact" | "external"
    /// </summary>
    public void LogToolCall(string userId, string toolName, string arguments, string toolType, string? traceId = null)
        => Write(userId, "tool_call", new
        {
            tool_name  = toolName,
            tool_type  = toolType,
            arguments  = Truncate(arguments, 500)
        }, traceId: traceId);

    /// <summary>
    /// 記錄 tool 執行後的結果（tool_result）。
    /// result: "success" | "error" | "skipped"
    /// </summary>
    public void LogToolResult(string userId, string toolName, string output, string result = "success", string? traceId = null)
        => Write(userId, "tool_result", new
        {
            tool_name = toolName,
            output    = Truncate(output, 500)
        }, result, traceId);

    // ── 查詢 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 查詢審計日誌，支援 limit + offset 分頁。
    /// userId 為 null 時查全域日誌（不限使用者）。
    /// 回傳 (Logs, Total) 其中 Total 為符合條件的總筆數。
    /// </summary>
    public (List<NyaAuditLog> Logs, int Total) GetLogs(
        string? userId = null,
        string? action = null,
        DateTime? fromDate = null,
        DateTime? toDate = null,
        int limit = 50,
        int offset = 0,
        string? keyword = null,
        string? result = null)
    {
        var (where, p) = Filter(userId, action, keyword, result, fromDate, toDate, limit, offset);
        var total = _db.Scalar<int>($"SELECT COUNT(*) FROM nya_audit_log {where}", p);
        var logs = _db.Query<NyaAuditLog>(
            $"SELECT * FROM nya_audit_log {where} ORDER BY created_at DESC LIMIT @limit OFFSET @offset", p);

        return (logs, total);
    }

    /// <summary>GetLogs / DeleteLogs 共用篩選——刪除範圍必須與列表顯示的筆數完全一致。</summary>
    private static (string Where, object Params) Filter(
        string? userId, string? action, string? keyword, string? result,
        DateTime? fromDate = null, DateTime? toDate = null, int limit = 0, int offset = 0)
    {
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(userId)) conditions.Add("user_id = @userId");
        if (!string.IsNullOrWhiteSpace(action)) conditions.Add("action = @action");
        // Phase 4-C 債4：result 維度過濾——管理端一鍵篩 error / skipped / success。
        if (!string.IsNullOrWhiteSpace(result)) conditions.Add("result = @result");
        if (fromDate.HasValue) conditions.Add("created_at >= @fromDate");
        if (toDate.HasValue) conditions.Add("created_at <= @toDate");
        // 稽核日誌改以關鍵字搜尋——對 detail_json / action / user_id 做 LIKE。
        if (!string.IsNullOrWhiteSpace(keyword))
            conditions.Add("(detail_json LIKE @keyword OR action LIKE @keyword OR user_id LIKE @keyword)");

        var where = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        var p = new
        {
            userId = userId ?? "",
            action = action ?? "",
            result = result ?? "",
            fromDate = fromDate ?? DateTime.MinValue,
            toDate = toDate ?? DateTime.MaxValue,
            keyword = $"%{keyword}%",
            limit,
            offset
        };
        return (where, p);
    }

    public NyaAuditLog? GetLogById(string logId)
        => _db.QueryFirst<NyaAuditLog>(
            "SELECT * FROM nya_audit_log WHERE log_id = @logId",
            new { logId });

    /// <summary>Phase 4-A：取某 trace 的整條鏈（一輪對話的所有稽核項，舊→新）。供「approval→原始對話」反查。</summary>
    public List<NyaAuditLog> GetByTraceId(string traceId)
        => string.IsNullOrWhiteSpace(traceId)
            ? new List<NyaAuditLog>()
            : _db.Query<NyaAuditLog>(
                "SELECT * FROM nya_audit_log WHERE trace_id = @traceId ORDER BY created_at ASC", new { traceId });

    // ── 刪除（管理端專用、不開放 LLM）───────────────────────────────────────────────────

    /// <summary>刪除單筆稽核日誌，回傳刪除筆數（0 = 不存在）。</summary>
    public int DeleteLog(string logId)
        => _db.Execute("DELETE FROM nya_audit_log WHERE log_id = @logId", new { logId });

    /// <summary>刪除符合篩選條件的稽核日誌（條件同 GetLogs；全部留空 = 清空全部），回傳刪除筆數。</summary>
    public int DeleteLogs(string? userId = null, string? action = null, string? keyword = null, string? result = null)
    {
        var (where, p) = Filter(userId, action, keyword, result);
        return _db.Execute($"DELETE FROM nya_audit_log {where}", p);
    }

    // ── 內部 ────────────────────────────────────────────────────────────────

    private void Write(string userId, string action, object? detail = null, string result = "success", string? traceId = null)
    {
        if (!_config.AuditEnabled)
            return;

        try
        {
            var log = new NyaAuditLog
            {
                LogId = $"nyal_{Guid.NewGuid():N}"[..24],
                UserId = userId,
                Action = action,
                DetailJson = detail != null
                    ? JsonSerializer.Serialize(detail)
                    : "{}",
                Result = result,
                TraceId = traceId ?? "",
                CreatedAt = DateTime.UtcNow
            };
            _db.Insert(log);
        }
        catch (Exception ex)
        {
            // Phase 4-C 債3：除了 server log，bump 兜底計數器讓寫入失敗可被監控觀測（不再完全靜默遺失）。
            System.Threading.Interlocked.Increment(ref _writeFailureCount);
            _logger.LogWarning(ex, "[NyaAudit] Failed to write audit log: action={Action} user={User} (total_failures={Failures})",
                action, userId, WriteFailureCount);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
