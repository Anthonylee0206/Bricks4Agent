using System.Text.Json;
using Broker.Services;
using BrokerCore;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;
using FunctionPool.Models;
using FunctionPool.Network;
using FunctionPool.Registry;

namespace Unit.Tests.Broker.Observability;

/// <summary>測試用的暫存 SQLite 資料庫，附上寫入「指定時間」資料的輔助方法。</summary>
internal sealed class TempBrokerDb : IDisposable
{
    private readonly string _path;

    private TempBrokerDb(string path, BrokerDb db)
    {
        _path = path;
        Db = db;
    }

    public BrokerDb Db { get; }

    public static TempBrokerDb Create()
    {
        var path = Path.Combine(Path.GetTempPath(), $"broker_incident_{Guid.NewGuid():N}.db");
        var db = new BrokerDb($"Data Source={path}");
        new BrokerDbInitializer(db).Initialize();
        return new TempBrokerDb(path, db);
    }

    public string AddExecution(string capabilityId, ExecutionState state, DateTime updatedAt, string? policyReason = null)
    {
        var request = new ExecutionRequest
        {
            RequestId = IdGen.New("req"),
            TaskId = "task_incident_test",
            SessionId = "ses_incident_test",
            PrincipalId = "prn_incident_test",
            CapabilityId = capabilityId,
            ExecutionStateValue = (int)state,
            PolicyReason = policyReason,
            TraceId = IdGen.New("trace"),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CreatedAt = updatedAt,
            UpdatedAt = updatedAt,
        };
        Db.Insert(request);
        return request.RequestId;
    }

    public string AddApproval(string capabilityId, DateTime createdAt, DateTime? expiresAt = null, ApprovalStatus status = ApprovalStatus.Pending)
    {
        var approval = new ApprovalRequest
        {
            ApprovalId = IdGen.New("apr"),
            RequestId = IdGen.New("req"),
            TaskId = "task_incident_test",
            SessionId = "ses_incident_test",
            PrincipalId = "prn_incident_test",
            CapabilityId = capabilityId,
            StatusValue = (int)status,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt ?? default,
            TraceId = IdGen.New("trace"),
        };
        Db.Insert(approval);
        return approval.ApprovalId;
    }

    public void AddAudit(string eventType, DateTime occurredAt, string? resourceRef = null)
        => Db.Insert(new AuditEvent
        {
            TraceId = IdGen.New("trace"),
            TraceSeq = 0,
            EventType = eventType,
            ResourceRef = resourceRef,
            PayloadDigest = "test",
            EventHash = Guid.NewGuid().ToString("N"),
            Details = "{}",
            OccurredAt = occurredAt,
        });

    public long MaxAuditEventId()
        => Db.Query<MaxIdRow>("SELECT COALESCE(MAX(event_id), 0) AS MaxId FROM audit_events", new { }).First().MaxId;

    public void AddHeartbeatLost(string workerId, DateTime observedAt)
        => Db.Insert(new ObservationEvent
        {
            ObservationId = IdGen.New("obs"),
            Source = ObservationSource.Internal,
            EventType = "WORKER_HEARTBEAT_LOST",
            TraceId = IdGen.New("trace"),
            WorkerId = workerId,
            Severity = ObservationSeverity.Warning,
            ObservedAt = observedAt,
        });

    public void AddSnapshot(DateTime capturedAt, int score, string status, int workerCount)
        => Db.Insert(new HealthScoreSnapshot
        {
            CapturedAt = capturedAt,
            OverallScore = score,
            OverallStatus = status,
            WorkerCount = workerCount,
        });

    public void AddInteraction(DateTime occurredAt, string? error, string rawInput = "使用者的私人訊息")
        => new HighLevelInteractionRecorder(Db).Record(new HighLevelInteractionRecord
        {
            Channel = "line",
            UserId = "U-incident-test",
            OccurredAt = new DateTimeOffset(DateTime.SpecifyKind(occurredAt, DateTimeKind.Utc)),
            RawInput = rawInput,
            RawReply = "回覆內容",
            Error = error,
        });

    public void Dispose()
    {
        Db.Dispose();
        foreach (var file in new[] { _path, _path + "-shm", _path + "-wal" })
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (IOException)
            {
                // 暫存檔刪不掉不影響測試結果
            }
        }
    }

    private sealed class MaxIdRow
    {
        public long MaxId { get; set; }
    }
}

/// <summary>可以指定回覆、並記下每次呼叫的假 LLM。</summary>
internal sealed class StubLlm : ILlmProxyService
{
    public bool IsEnabled { get; set; } = true;

    public Func<string, LlmChatResult> Reply { get; set; } = _ => new LlmChatResult { Content = "", Model = "stub-model" };

    public List<(string UserPrompt, BrokerTask? Task)> Calls { get; } = new();

    public List<string> SystemPrompts { get; } = new();

    public AgentRuntimeSpec BuildRuntimeSpec(BrokerTask? task = null, IEnumerable<string>? capabilityIds = null) => new();

    public Task<bool> HealthCheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsEnabled);

    public Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LlmModelInfo>>([]);

    public Task<LlmChatResult> ChatAsync(JsonElement body, BrokerTask? task = null, CancellationToken cancellationToken = default)
    {
        var userPrompt = body.GetProperty("messages")[1].GetProperty("content").GetString() ?? "";
        SystemPrompts.Add(body.GetProperty("messages")[0].GetProperty("content").GetString() ?? "");
        Calls.Add((userPrompt, task));
        return Task.FromResult(Reply(userPrompt));
    }
}

/// <summary>只回報一份固定 worker 清單的 registry。</summary>
internal sealed class FakeRegistry : IWorkerRegistry
{
    private readonly List<WorkerInfo> _workers = new();

    public FakeRegistry Add(string id, params string[] capabilities)
    {
        _workers.Add(new WorkerInfo
        {
            WorkerId = id,
            Capabilities = capabilities.ToList(),
            State = WorkerState.Ready,
            LastHeartbeat = DateTime.UtcNow,
        });
        return this;
    }

    public List<WorkerInfo> GetAllWorkers() => _workers.ToList();
    public List<WorkerInfo> GetWorkersByCapability(string capabilityId) =>
        _workers.Where(w => w.Capabilities.Contains(capabilityId)).ToList();
    public bool HasAvailableWorker(string capabilityId) => GetWorkersByCapability(capabilityId).Any(w => w.IsAvailable);
    public int GetAvailableCount(string capabilityId) => GetWorkersByCapability(capabilityId).Count(w => w.IsAvailable);

    public bool Register(WorkerInfo worker, WorkerConnection connection) => throw new NotSupportedException();
    public bool Deregister(string workerId) => throw new NotSupportedException();
    public WorkerConnection? GetAvailableWorker(string capabilityId) => throw new NotSupportedException();
    public void UpdateHeartbeat(string workerId) => throw new NotSupportedException();
    public void IncrementActiveTask(string workerId) => throw new NotSupportedException();
    public void DecrementActiveTask(string workerId) => throw new NotSupportedException();
    public void SetWorkerState(string workerId, WorkerState state) => throw new NotSupportedException();
}
