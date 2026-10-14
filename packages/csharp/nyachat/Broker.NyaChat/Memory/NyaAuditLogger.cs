using System.Text.Json;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;

namespace Broker.NyaChat;

public class NyaAuditLogger
{
    public void LogMemoryTool(string userId, string tool, string arguments, string result, string? traceId = null)
        => Write(userId, tool, new { arguments, result }, traceId: traceId);
    private static readonly AsyncLocal<object?> _operator = new();

    public IDisposable BeginAdminOperation(string username, string role)
    {
        var previous = _operator.Value;
        _operator.Value = new { username, role };
        return new OperatorScope(previous);
    }

    private sealed class OperatorScope(object? previous) : IDisposable
    {
        public void Dispose() => _operator.Value = previous;
    }
    private readonly BrokerDb _db;
    private readonly INyaConfigStore<NyaChatConfig> _configStore;
    private NyaChatConfig _config => _configStore.Current;
    private readonly ILogger<NyaAuditLogger> _logger;

    private long _writeFailureCount;

    public long WriteFailureCount => System.Threading.Interlocked.Read(ref _writeFailureCount);

    public NyaAuditLogger(BrokerDb db, INyaConfigStore<NyaChatConfig> configStore, ILogger<NyaAuditLogger> logger)
    {
        _db = db;
        _configStore = configStore;
        _logger = logger;
        _db.EnsureTable<NyaAuditLog>();

        EnsureColumn("nya_audit_log", "trace_id", "TEXT");
    }

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

    public void LogChatInput(string userId, string message, string model,
        int factsCount, int summariesCount, int historyCount, int totalLlmMessages,
        int estimatedTokens = 0, int maxContextTokens = 0, bool truncated = false, string? traceId = null,
        object? selection = null, object? execution = null)
        => Write(userId, "chat_input", new
        {
            message,
            model,
            selection,
            execution,
            context = new
            {
                facts = factsCount,
                summaries = summariesCount,
                history = historyCount,
                total_llm_messages = totalLlmMessages,

                estimated_tokens = estimatedTokens,
                max_context_tokens = maxContextTokens,
                truncated
            }
        }, traceId: traceId);

    public void LogLlmRequest(string userId, string traceId, string task, string provider, NyaLlmProfile profile, NyaLlmRequest request)
        => Write(userId, "llm_request", new
        {
            task, provider, profile_id = profile.ProfileId, model = profile.Model,
            max_tokens = request.MaxTokens, temperature = request.Temperature, top_p = request.TopP,
            model_overrides = profile.ModelOverrides, enable_thinking = profile.EnableThinking, thinking_budget = profile.ThinkingBudget,
            messages = request.Messages.Select(m => new { role = m.Role, content = m.Content, name = m.Name, tool_call_id = m.ToolCallId, tool_calls = m.ToolCalls }).ToArray(),
            function_tools = request.Tools
        }, traceId: traceId);

    public void LogChatOutput(string userId, string reply, string model, long latencyMs, int replyCount, string? traceId = null)
        => Write(userId, "chat_output", new
        {
            reply_preview = Truncate(reply, 100),
            model,
            latency_ms = latencyMs,
            part_count = replyCount
        }, traceId: traceId);

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

    public void LogRoleChange(string userId, string action, string roleId)
        => Write(userId, action, new { role_id = roleId });

    public void LogError(string userId, string action, string error, string? traceId = null)
        => Write(userId, "error", new { original_action = action, error = Truncate(error, 300) }, "error", traceId);

    public void LogAuditPurge(string scope, int deletedCount, string? target = null, object? filter = null)
        => Write("__sys__", "audit_purge", new { scope, deleted = deletedCount, target, filter });

    public void LogToolToggle(string toolId, bool enabled)
        => Write("__sys__", "tool_toggle", new { tool_id = toolId, enabled });

    public void LogSoulChange(string action, string? soulId, string? userId = null)
        => Write(userId ?? "__sys__", action, new { soul_id = soulId, target_user_id = userId });

    public void LogReload(string action, int loaded, object? failures = null, string result = "success")
        => Write("__sys__", action, new { loaded, failures }, result);

    public void LogConfigUpdate(IReadOnlyList<string> changedKeys)
        => Write("__sys__", "config_update", new { changed = changedKeys });

    public void LogLlmProfileUpsert(string profileId, string provider, string model)
        => Write("__sys__", "llm_profile_upsert", new { profile_id = profileId, provider, model });

    public void LogLlmProfileDelete(string profileId)
        => Write("__sys__", "llm_profile_delete", new { profile_id = profileId });

    public void LogLlmRouteSet(string callSiteId, string profileId)
        => Write("__sys__", "llm_route_set", new { call_site_id = callSiteId, profile_id = profileId });

    public void LogToolCall(string userId, string toolName, string arguments, string toolType, string? traceId = null)
        => Write(userId, "tool_call", new
        {
            tool_name  = toolName,
            tool_type  = toolType,
            arguments  = Truncate(arguments, 500)
        }, traceId: traceId);

    public void LogToolResult(string userId, string toolName, string output, string result = "success", string? traceId = null)
        => Write(userId, "tool_result", new
        {
            tool_name = toolName,
            output    = Truncate(output, 500)
        }, result, traceId);

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

    private static (string Where, object Params) Filter(
        string? userId, string? action, string? keyword, string? result,
        DateTime? fromDate = null, DateTime? toDate = null, int limit = 0, int offset = 0)
    {
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(userId)) conditions.Add("user_id = @userId");
        if (!string.IsNullOrWhiteSpace(action)) conditions.Add("action = @action");

        if (!string.IsNullOrWhiteSpace(result)) conditions.Add("result = @result");
        if (fromDate.HasValue) conditions.Add("created_at >= @fromDate");
        if (toDate.HasValue) conditions.Add("created_at <= @toDate");

        if (!string.IsNullOrWhiteSpace(keyword))
            conditions.Add("(detail_json LIKE @keyword OR action LIKE @keyword OR user_id LIKE @keyword OR log_id LIKE @keyword OR trace_id LIKE @keyword)");

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

    public List<NyaAuditLog> GetByTraceId(string traceId)
        => string.IsNullOrWhiteSpace(traceId)
            ? new List<NyaAuditLog>()
            : _db.Query<NyaAuditLog>(
                "SELECT * FROM nya_audit_log WHERE trace_id = @traceId ORDER BY created_at ASC", new { traceId });

    public NyaAuditLog? GetLastChatInput(string userId)
        => _db.QueryFirst<NyaAuditLog>(
            "SELECT * FROM nya_audit_log WHERE user_id = @userId AND action = 'chat_input' ORDER BY created_at DESC LIMIT 1", new { userId });

    public int DeleteLog(string logId)
        => _db.Execute("DELETE FROM nya_audit_log WHERE log_id = @logId", new { logId });

    public int DeleteLogs(string? userId = null, string? action = null, string? keyword = null, string? result = null)
    {
        var (where, p) = Filter(userId, action, keyword, result);
        return _db.Execute($"DELETE FROM nya_audit_log {where}", p);
    }

    private void Write(string userId, string action, object? detail = null, string result = "success", string? traceId = null)
    {
        if (!_config.AuditEnabled)
            return;

        try
        {
            var details = JsonSerializer.SerializeToNode(detail) as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject();
            if (_operator.Value != null)
                details["operator"] = JsonSerializer.SerializeToNode(_operator.Value);
            var log = new NyaAuditLog
            {
                LogId = $"nyal_{Guid.NewGuid():N}"[..24],
                UserId = userId,
                Action = action,
                DetailJson = details.ToJsonString(),
                Result = result,
                TraceId = traceId ?? "",
                CreatedAt = DateTime.UtcNow
            };
            _db.Insert(log);
        }
        catch (Exception ex)
        {

            System.Threading.Interlocked.Increment(ref _writeFailureCount);
            _logger.LogWarning(ex, "[NyaAudit] Failed to write audit log: action={Action} user={User} (total_failures={Failures})",
                action, userId, WriteFailureCount);
        }
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
