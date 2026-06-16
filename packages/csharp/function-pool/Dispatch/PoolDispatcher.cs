using System.Diagnostics;
using System.Text.Json;
using BrokerCore;
using CacheProtocol;
using BrokerCore.Contracts;
using BrokerCore.Services;
using FunctionPool.Models;
using FunctionPool.Registry;
using Microsoft.Extensions.Logging;

namespace FunctionPool.Dispatch;

/// <summary>
/// 功能池分派器 — IExecutionDispatcher 實作
///
/// 職責：
/// 1. 接收 ApprovedRequest → 按 CapabilityId 查詢 WorkerRegistry
/// 2. Round-Robin 選取可用 Worker → 發送 WORKER_EXECUTE
/// 3. 等待 WORKER_RESULT（含超時）
/// 4. 失敗重試（嘗試下一個 Worker 實例）
/// 5. 所有 Worker 不可用 → 返回失敗
///
/// Tracing：若 ctor 注入 IAuditService、Dispatcher 會在派發前後各記一筆 audit event，
/// 細節（worker_id / 嘗試次數 / 耗時 ms）寫進 details JSON。Dashboard 走 dispatcher 的
/// 路徑（StrategyEndpoints / AutoTrader…）跟 BrokerService 16 步驟那條重型路徑都會被追到。
/// 沒設 TraceId 的請求會自動產一個（"trc_..."）以撐起 audit hash chain。
/// </summary>
public class PoolDispatcher : IExecutionDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private readonly IWorkerRegistry _registry;
    private readonly PoolConfig _config;
    private readonly ILogger<PoolDispatcher> _logger;
    private readonly IAuditService? _audit;
    private readonly ICapabilityAclService? _acl;
    private readonly ITradingApprovalService? _approval;
    private readonly IShutdownState? _shutdown;

    public PoolDispatcher(
        IWorkerRegistry registry,
        PoolConfig config,
        ILogger<PoolDispatcher> logger,
        IAuditService? audit = null,
        ICapabilityAclService? acl = null,
        ITradingApprovalService? approval = null,
        IShutdownState? shutdown = null)
    {
        _registry = registry;
        _config = config;
        _logger = logger;
        _audit = audit;
        _acl = acl;
        _approval = approval;
        _shutdown = shutdown;
    }

    /// <summary>是否有指定能力的可用 Worker</summary>
    public bool HasAvailableWorker(string capabilityId)
    {
        return _registry.HasAvailableWorker(capabilityId);
    }

    public async Task<ExecutionResult> DispatchAsync(ApprovedRequest request)
    {
        // Dashboard 直呼路徑（StrategyEndpoints 等）常常沒設 TraceId、但 audit hash chain
        // 需要 trace_id 為 key——這裡補一個就好，不要求 caller 配合。
        var traceId = string.IsNullOrEmpty(request.TraceId) ? IdGen.New("trc") : request.TraceId;
        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        // ── Graceful shutdown gate ──
        // broker 正在收尾、不接新派發、避免 in-flight dispatch 撞到 worker connection drop。
        // Caller 看到明確的 "broker shutting down"、不必猜為什麼 worker 沒回。
        if (_shutdown?.IsStopping == true)
        {
            TryAudit(traceId, "DISPATCH_DENIED", request, details: new
            {
                capability = request.CapabilityId,
                reason = "broker shutting down",
            });
            return ExecutionResult.Fail(request.RequestId,
                "Broker is shutting down; dispatch refused.");
        }

        // ── ACL 檢查（fail-open by design）──
        // role 為空 / admin / system → 永遠 allow；只有 explicit 非 admin role 走白名單檢查。
        // 不通過 → 寫 DISPATCH_DENIED 進 audit chain、回 ExecutionResult.Fail。
        if (_acl != null && !_acl.IsAllowed(request.PrincipalId, request.Role, request.CapabilityId))
        {
            _logger.LogWarning(
                "ACL denied: role='{Role}' principal='{Pid}' capability='{Cap}'",
                request.Role, request.PrincipalId, request.CapabilityId);
            TryAudit(traceId, "DISPATCH_DENIED", request, details: new
            {
                capability = request.CapabilityId,
                role       = request.Role,
                reason     = "Capability not allowed for role",
            });
            return ExecutionResult.Fail(request.RequestId,
                $"Capability '{request.CapabilityId}' is not allowed for role '{request.Role}'");
        }

        // ── Approval gate（policy by capability + route）──
        // 高風險 capability/route 需要 admin 在 dashboard 點 approve 才放行：
        //   - capability 級：trading.order（整個 capability 都受控）
        //   - route 級：trading.perpetual::place_order/cancel_order/set_leverage
        //     （讓同 capability 內讀寫分離：account/positions 仍可讀、寫操作需審）
        // 沒設 approval service 或 (capability,route) 不在受控集合 → skip。
        // system principal 跳過 approval（內部背景任務、AutoTrader 等不會被擋）。
        if (_approval != null && _approval.RequiresApproval(request.CapabilityId, request.Route)
            && !string.Equals(request.PrincipalId, "system", StringComparison.OrdinalIgnoreCase))
        {
            var apr = _approval.GetOrCreatePending(
                traceId, request.CapabilityId, request.Route, request.Payload,
                request.PrincipalId, request.Role);
            switch (apr.Status)
            {
                case "approved":
                    // 放行、繼續派發（會走後面的 STARTED → SUCCEEDED/FAILED 流程）
                    _logger.LogInformation("Approval {Id} approved by {By}, proceeding dispatch",
                        apr.ApprovalId, apr.DecidedBy);
                    TryAudit(traceId, "DISPATCH_APPROVED", request, details: new
                    {
                        approval_id = apr.ApprovalId,
                        decided_by = apr.DecidedBy,
                    });
                    break;
                case "rejected":
                    TryAudit(traceId, "DISPATCH_DENIED", request, details: new
                    {
                        approval_id = apr.ApprovalId,
                        reason = "Rejected by admin: " + (apr.DecisionReason ?? ""),
                    });
                    return ExecutionResult.Fail(request.RequestId,
                        $"Approval rejected by {apr.DecidedBy}: {apr.DecisionReason}");
                default: // pending
                    TryAudit(traceId, "DISPATCH_PENDING_APPROVAL", request, details: new
                    {
                        approval_id = apr.ApprovalId,
                        capability = request.CapabilityId,
                        principal_id = request.PrincipalId,
                    });
                    return ExecutionResult.Fail(request.RequestId,
                        $"Pending admin approval (approval_id={apr.ApprovalId}). Retry the same trace_id after approval.");
            }
        }

        TryAudit(traceId, "DISPATCH_STARTED", request, details: new
        {
            capability = request.CapabilityId,
            route      = request.Route,
            attempts_max = _config.MaxRetries + 1,
            started_at = startedAt,
        });

        ExecutionResult result;
        string? finalWorkerId = null;
        var attemptsUsed = 0;

        for (int attempt = 0; attempt <= _config.MaxRetries; attempt++)
        {
            attemptsUsed = attempt + 1;
            var conn = _registry.GetAvailableWorker(request.CapabilityId);
            if (conn == null)
            {
                _logger.LogDebug(
                    "No available worker for capability '{Cap}' (attempt {A})",
                    request.CapabilityId, attempt);
                result = ExecutionResult.Fail(request.RequestId,
                    $"No available worker for capability '{request.CapabilityId}'");
                EmitCompleted(traceId, request, result, finalWorkerId, attemptsUsed, sw);
                return result;
            }

            finalWorkerId = conn.WorkerId;

            try
            {
                // 增加活躍任務計數
                _registry.IncrementActiveTask(conn.WorkerId);

                // 編碼 WORKER_EXECUTE frame
                var executeCmd = new WorkerExecuteCommand
                {
                    RequestId = request.RequestId,
                    CapabilityId = request.CapabilityId,
                    Route = request.Route,
                    Payload = request.Payload,
                    Scope = request.Scope,
                    TraceId = traceId
                };

                var payload = JsonSerializer.SerializeToUtf8Bytes(executeCmd, JsonOptions);
                var frame = FrameCodec.Encode(OpCodes.WORKER_EXECUTE, payload);

                _logger.LogDebug(
                    "Dispatching to worker {W}: requestId={R} capability={C} route={Route} trace={T}",
                    conn.WorkerId, request.RequestId, request.CapabilityId, request.Route, traceId);

                // 發送並等待結果（含 timeout）
                var (respOpCode, respPayload) = await conn.SendAndWaitAsync(
                    frame, request.RequestId, _config.DispatchTimeout);

                // 成功完成 → 歸還活躍任務計數
                _registry.DecrementActiveTask(conn.WorkerId);

                result = ParseExecutionResult(request.RequestId, respOpCode, respPayload);
                EmitCompleted(traceId, request, result, finalWorkerId, attemptsUsed, sw);
                return result;
            }
            catch (TimeoutException ex)
            {
                _logger.LogWarning(
                    "Worker dispatch timeout for request {R}: {Error}",
                    request.RequestId, ex.Message);

                // 超時 → 減少活躍任務計數（可能 Worker 仍在執行，但我們放棄等待）
                _registry.DecrementActiveTask(conn.WorkerId);

                if (attempt < _config.MaxRetries)
                    continue;
            }
            catch (Exception ex) when (attempt < _config.MaxRetries)
            {
                _logger.LogWarning(ex,
                    "Worker dispatch failed for request {R} (attempt {A}), retrying",
                    request.RequestId, attempt + 1);

                // 異常重試 → 歸還活躍任務計數
                _registry.DecrementActiveTask(conn.WorkerId);
                continue;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Worker dispatch failed for request {R}, no more retries",
                    request.RequestId);

                // 最終失敗 → 歸還活躍任務計數
                _registry.DecrementActiveTask(conn.WorkerId);

                result = ExecutionResult.Fail(request.RequestId,
                    $"Worker dispatch failed: {ex.Message}");
                EmitCompleted(traceId, request, result, finalWorkerId, attemptsUsed, sw);
                return result;
            }
        }

        result = ExecutionResult.Fail(request.RequestId, "All worker dispatch attempts failed");
        EmitCompleted(traceId, request, result, finalWorkerId, attemptsUsed, sw);
        return result;
    }

    // ── Audit emission helpers ─────────────────────────────────────────

    private void EmitCompleted(
        string traceId, ApprovedRequest request, ExecutionResult result,
        string? workerId, int attempts, Stopwatch sw)
    {
        sw.Stop();
        TryAudit(traceId, result.Success ? "DISPATCH_SUCCEEDED" : "DISPATCH_FAILED", request, details: new
        {
            capability  = request.CapabilityId,
            route       = request.Route,
            worker_id   = workerId,
            attempts,
            duration_ms = sw.ElapsedMilliseconds,
            error       = result.Success ? null : result.ErrorMessage,
        });
    }

    private void TryAudit(string traceId, string eventType, ApprovedRequest request, object details)
    {
        if (_audit == null) return;
        try
        {
            _audit.RecordEvent(
                traceId: traceId,
                eventType: eventType,
                principalId: string.IsNullOrEmpty(request.PrincipalId) ? null : request.PrincipalId,
                taskId: string.IsNullOrEmpty(request.TaskId) ? null : request.TaskId,
                sessionId: string.IsNullOrEmpty(request.SessionId) ? null : request.SessionId,
                resourceRef: request.CapabilityId,
                details: JsonSerializer.Serialize(details, JsonOptions));
        }
        catch (Exception ex)
        {
            // Audit 是 best-effort、寫不進去也不能擋住 dispatch
            _logger.LogWarning(ex, "Failed to record dispatch audit event {E} for trace {T}", eventType, traceId);
        }
    }

    /// <summary>解析 WORKER_RESULT payload → ExecutionResult</summary>
    private ExecutionResult ParseExecutionResult(
        string requestId, byte opCode, ReadOnlyMemory<byte> payload)
    {
        if (opCode != OpCodes.WORKER_RESULT)
        {
            return ExecutionResult.Fail(requestId,
                $"Unexpected response OpCode: 0x{opCode:X2}");
        }

        try
        {
            var result = JsonSerializer.Deserialize<WorkerResultMessage>(
                payload.Span, JsonOptions);

            if (result == null)
                return ExecutionResult.Fail(requestId, "Failed to deserialize worker result");

            if (result.Success)
            {
                return ExecutionResult.Ok(requestId, result.ResultPayload ?? "{}");
            }
            else
            {
                return ExecutionResult.Fail(requestId, result.Error ?? "Worker execution failed");
            }
        }
        catch (Exception ex)
        {
            return ExecutionResult.Fail(requestId,
                $"Failed to parse worker result: {ex.Message}");
        }
    }
}

// ── 協議訊息 DTO ──

/// <summary>WORKER_EXECUTE payload（Broker → Worker）</summary>
public class WorkerExecuteCommand
{
    public string RequestId { get; set; } = string.Empty;
    public string CapabilityId { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public string Scope { get; set; } = "{}";
    public string TraceId { get; set; } = string.Empty;
}

/// <summary>WORKER_RESULT payload（Worker → Broker）</summary>
public class WorkerResultMessage
{
    public string RequestId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ResultPayload { get; set; }
    public string? Error { get; set; }
}
