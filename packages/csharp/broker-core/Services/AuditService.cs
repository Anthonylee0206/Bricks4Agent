using System.Security.Cryptography;
using System.Text;
using BrokerCore.Data;
using BrokerCore.Models;

namespace BrokerCore.Services;

/// <summary>
/// 追加式稽核服務 —— per-trace hash chain
///
/// 並發語意：
/// - 同 trace 內靠 exclusive transaction + UNIQUE(trace_id, trace_seq) 序列化
/// - 跨 trace 不阻塞
/// - 只 INSERT 不 UPDATE/DELETE → 防篡改
///
/// Hash Chain：
/// event_hash = SHA256(previous_event_hash + trace_id + trace_seq + event_type + payload_digest + occurred_at)
/// 首筆事件的 previous_event_hash = "GENESIS"
/// </summary>
public class AuditService : IAuditService
{
    private readonly BrokerDb _db;

    public AuditService(BrokerDb db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public AuditEvent RecordEvent(
        string traceId,
        string eventType,
        string? principalId = null,
        string? taskId = null,
        string? sessionId = null,
        string? resourceRef = null,
        string details = "{}")
    {
        var payloadDigest = ComputeSha256(details);
        var occurredAt = DateTime.UtcNow;

        // exclusive transaction 保證同 trace 寫入序列化
        return _db.InTransaction(() =>
        {
            // 取得當前 trace 的最後一筆事件
            var lastEvent = _db.QueryFirst<AuditEvent>(
                "SELECT * FROM audit_events WHERE trace_id = @traceId ORDER BY trace_seq DESC LIMIT 1",
                new { traceId });

            var traceSeq = lastEvent != null ? lastEvent.TraceSeq + 1 : 0;
            var previousHash = lastEvent?.EventHash ?? "GENESIS";

            // 計算 event_hash
            var hashInput = $"{previousHash}|{traceId}|{traceSeq}|{eventType}|{payloadDigest}|{occurredAt:O}";
            var eventHash = ComputeSha256(hashInput);

            var auditEvent = new AuditEvent
            {
                TraceId = traceId,
                TraceSeq = traceSeq,
                EventType = eventType,
                PrincipalId = principalId,
                TaskId = taskId,
                SessionId = sessionId,
                ResourceRef = resourceRef,
                PayloadDigest = payloadDigest,
                PreviousEventHash = previousHash,
                EventHash = eventHash,
                Details = details,
                OccurredAt = occurredAt
            };

            _db.Insert(auditEvent);
            return auditEvent;
        });
    }

    /// <inheritdoc />
    public List<AuditEvent> GetTraceEvents(string traceId)
    {
        return _db.Query<AuditEvent>(
            "SELECT * FROM audit_events WHERE trace_id = @traceId ORDER BY trace_seq ASC",
            new { traceId });
    }

    /// <inheritdoc />
    public bool VerifyTraceIntegrity(string traceId)
    {
        var events = GetTraceEvents(traceId);

        if (events.Count == 0)
            return true; // 空 trace 視為完整

        for (int i = 0; i < events.Count; i++)
        {
            var ev = events[i];

            // 驗證序號連續
            if (ev.TraceSeq != i)
                return false;

            // 驗證 previous_event_hash 鏈結
            var expectedPrevHash = i == 0 ? "GENESIS" : events[i - 1].EventHash;
            if (ev.PreviousEventHash != expectedPrevHash)
                return false;

            // 重新計算 hash 並驗證
            // [修正 2026-10 AnthonyLee] 寫入時 occurredAt 是 DateTime.UtcNow（Kind=Utc），「:O」格式帶結尾的 "Z"；
            // 從 SQLite 讀回時 Kind 變成 Unspecified，「:O」就不帶 "Z"，字串不同、雜湊必不相同，
            // 任何正常的鏈都會驗證失敗。所有事件寫入時都是 UTC，這裡補回 Kind 再格式化，與寫入端一致。
            var occurredAtUtc = ev.OccurredAt.Kind == DateTimeKind.Utc
                ? ev.OccurredAt
                : DateTime.SpecifyKind(ev.OccurredAt, DateTimeKind.Utc);
            var hashInput = $"{ev.PreviousEventHash}|{ev.TraceId}|{ev.TraceSeq}|{ev.EventType}|{ev.PayloadDigest}|{occurredAtUtc:O}";
            var expectedHash = ComputeSha256(hashInput);
            if (ev.EventHash != expectedHash)
                return false;
        }

        return true;
    }

    /// <inheritdoc />
    public List<AuditEvent> QueryEvents(
        string? eventType = null, string? principalId = null,
        string? taskId = null, int offset = 0, int limit = 50)
    {
        var sql = new StringBuilder("SELECT * FROM audit_events WHERE 1=1");
        var parameters = new Dictionary<string, object>();

        if (!string.IsNullOrEmpty(eventType))
        {
            sql.Append(" AND event_type = @eventType");
            parameters["eventType"] = eventType;
        }
        if (!string.IsNullOrEmpty(principalId))
        {
            sql.Append(" AND principal_id = @principalId");
            parameters["principalId"] = principalId;
        }
        if (!string.IsNullOrEmpty(taskId))
        {
            sql.Append(" AND task_id = @taskId");
            parameters["taskId"] = taskId;
        }

        sql.Append(" ORDER BY event_id DESC LIMIT @limit OFFSET @offset");
        parameters["limit"] = limit;
        parameters["offset"] = offset;

        return _db.Query<AuditEvent>(sql.ToString(), parameters);
    }

    // ── 內部方法 ──

    private static string ComputeSha256(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
