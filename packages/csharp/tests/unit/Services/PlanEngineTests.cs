using BrokerCore.Services;
using BrokerCore.Models;
using Unit.Tests.Helpers;

namespace Unit.Tests.Services;

/// <summary>
/// PlanEngine 確定性流程測試。
///
/// PlanEngine 本身是純協調層（無獨立純函式）；DAG 建構 / Kahn 拓撲排序 /
/// 就緒節點 / 狀態轉移等確定性邏輯實作在 PlanService 內。因此這裡以
/// 「真實 in-memory PlanService（TestDb.CreateInMemory）」+「替身 IBrokerService /
/// ISharedContextService」組裝 PlanEngine，透過 SubmitAndExecuteAsync 端到端
/// 驗證確定性行為：拓撲執行順序、Plan/Node 狀態機、deadlock、下游級聯取消、
/// 重試、DataFlow 寫出、取消權杖。所有輸入皆確定性、無時間/IO 競態。
/// </summary>
public class PlanEngineTests : IDisposable
{
    private readonly global::BrokerCore.Data.BrokerDb _db;
    private readonly IAuditService _audit;
    private readonly PlanService _planService;
    private readonly IBrokerService _broker;
    private readonly ISharedContextService _context;

    public PlanEngineTests()
    {
        _db = TestDb.CreateInMemory();
        // RecordEvent 回傳值在 PlanService / PlanEngine 中皆未被使用，無需 stub 回傳值
        _audit = Substitute.For<IAuditService>();

        _planService = new PlanService(_db, _audit);
        _broker = Substitute.For<IBrokerService>();
        _context = Substitute.For<ISharedContextService>();
    }

    public void Dispose() => _db.Dispose();

    // ── 測試輔助 ──

    private PlanEngine CreateEngine() =>
        new PlanEngine(_planService, _broker, _context, _audit);

    /// <summary>所有 capability 一律回傳 Succeeded 的 broker 替身行為。</summary>
    private void BrokerAlwaysSucceeds()
    {
        _broker.SubmitExecutionRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci => Task.FromResult(new ExecutionRequest
            {
                RequestId = "req_" + Guid.NewGuid().ToString("N"),
                ExecutionState = ExecutionState.Succeeded,
                ResultPayload = "{\"ok\":true}"
            }));
    }

    /// <summary>指定 capabilityId 回傳特定狀態，其餘 Succeeded。</summary>
    private void BrokerStateFor(string capabilityId, ExecutionState state, string? reason = null)
    {
        _broker.SubmitExecutionRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci =>
            {
                var cap = ci.ArgAt<string>(3);
                if (cap == capabilityId)
                {
                    return Task.FromResult(new ExecutionRequest
                    {
                        RequestId = "req_" + Guid.NewGuid().ToString("N"),
                        ExecutionState = state,
                        PolicyReason = reason,
                        ResultPayload = reason ?? "{}"
                    });
                }
                return Task.FromResult(new ExecutionRequest
                {
                    RequestId = "req_" + Guid.NewGuid().ToString("N"),
                    ExecutionState = ExecutionState.Succeeded,
                    ResultPayload = "{\"ok\":true}"
                });
            });
    }

    private Plan NewDraftPlan(string taskId = "task_1") =>
        _planService.CreatePlan(taskId, "p_owner", "title", null);

    private PlanNode AddNode(string planId, string cap, string? outputKey = null, int maxRetries = 1) =>
        _planService.AddNode(planId, cap, "intent-" + cap, "{}", outputKey, maxRetries);

    private NodeState NodeStateOf(string planId, string nodeId) =>
        _planService.GetNodes(planId).Single(n => n.NodeId == nodeId).State;

    private PlanState PlanStateOf(string planId) =>
        _planService.GetPlan(planId)!.State;

    // ─────────────────────────────────────────────────────────
    // 正常路徑：線性 DAG 拓撲執行 → Completed
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitAndExecute_LinearDag_AllNodesSucceed_PlanCompleted()
    {
        BrokerAlwaysSucceeds();
        var plan = NewDraftPlan();
        var a = AddNode(plan.PlanId, "cap.a");
        var b = AddNode(plan.PlanId, "cap.b");
        var c = AddNode(plan.PlanId, "cap.c");
        _planService.AddEdge(plan.PlanId, a.NodeId, b.NodeId, EdgeType.ControlFlow, null, null);
        _planService.AddEdge(plan.PlanId, b.NodeId, c.NodeId, EdgeType.ControlFlow, null, null);

        var result = await CreateEngine().SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        result.State.Should().Be(PlanState.Completed);
        NodeStateOf(plan.PlanId, a.NodeId).Should().Be(NodeState.Succeeded);
        NodeStateOf(plan.PlanId, b.NodeId).Should().Be(NodeState.Succeeded);
        NodeStateOf(plan.PlanId, c.NodeId).Should().Be(NodeState.Succeeded);
    }

    [Fact]
    public async Task SubmitAndExecute_LinearDag_ExecutesInTopologicalOrder()
    {
        var order = new List<string>();
        _broker.SubmitExecutionRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci =>
            {
                order.Add(ci.ArgAt<string>(3)); // capabilityId
                return Task.FromResult(new ExecutionRequest
                {
                    RequestId = "req_" + Guid.NewGuid().ToString("N"),
                    ExecutionState = ExecutionState.Succeeded,
                    ResultPayload = "{}"
                });
            });

        var plan = NewDraftPlan();
        // 故意以非拓撲順序加入節點，依賴 ValidateDag 計算 Ordinal
        var c = AddNode(plan.PlanId, "cap.c");
        var a = AddNode(plan.PlanId, "cap.a");
        var b = AddNode(plan.PlanId, "cap.b");
        _planService.AddEdge(plan.PlanId, a.NodeId, b.NodeId, EdgeType.ControlFlow, null, null);
        _planService.AddEdge(plan.PlanId, b.NodeId, c.NodeId, EdgeType.ControlFlow, null, null);

        await CreateEngine().SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        order.Should().Equal("cap.a", "cap.b", "cap.c");
    }

    [Fact]
    public async Task SubmitAndExecute_DiamondDag_AllSucceed_JoinNodeRunsLast()
    {
        var order = new List<string>();
        _broker.SubmitExecutionRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci =>
            {
                order.Add(ci.ArgAt<string>(3));
                return Task.FromResult(new ExecutionRequest
                {
                    RequestId = "req_" + Guid.NewGuid().ToString("N"),
                    ExecutionState = ExecutionState.Succeeded,
                    ResultPayload = "{}"
                });
            });

        var plan = NewDraftPlan();
        var a = AddNode(plan.PlanId, "cap.a");
        var b = AddNode(plan.PlanId, "cap.b");
        var cNode = AddNode(plan.PlanId, "cap.c");
        var d = AddNode(plan.PlanId, "cap.d");
        // A → B, A → C, B → D, C → D （菱形）
        _planService.AddEdge(plan.PlanId, a.NodeId, b.NodeId, EdgeType.ControlFlow, null, null);
        _planService.AddEdge(plan.PlanId, a.NodeId, cNode.NodeId, EdgeType.ControlFlow, null, null);
        _planService.AddEdge(plan.PlanId, b.NodeId, d.NodeId, EdgeType.ControlFlow, null, null);
        _planService.AddEdge(plan.PlanId, cNode.NodeId, d.NodeId, EdgeType.ControlFlow, null, null);

        var result = await CreateEngine().SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        result.State.Should().Be(PlanState.Completed);
        order.Should().HaveCount(4);
        order[0].Should().Be("cap.a"); // 唯一無入邊者必先
        order[3].Should().Be("cap.d"); // join 節點必最後
        order.Should().Contain(new[] { "cap.b", "cap.c" });
    }

    // ─────────────────────────────────────────────────────────
    // 失敗 / 狀態轉移路徑
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitAndExecute_PepDenied_NodeFailed_PlanFailed()
    {
        BrokerStateFor("cap.a", ExecutionState.Denied, reason: "blocked");
        var plan = NewDraftPlan();
        var a = AddNode(plan.PlanId, "cap.a");

        var result = await CreateEngine().SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        result.State.Should().Be(PlanState.Failed);
        NodeStateOf(plan.PlanId, a.NodeId).Should().Be(NodeState.Failed);
    }

    [Fact]
    public async Task SubmitAndExecute_NodeFailsNoRetriesLeft_DownstreamCascadeCancelled()
    {
        // A 失敗（maxRetries=1 → 無重試），B、C 為下游 Pending → 應被級聯取消
        BrokerStateFor("cap.a", ExecutionState.Failed, reason: "boom");
        var plan = NewDraftPlan();
        var a = AddNode(plan.PlanId, "cap.a", maxRetries: 1);
        var b = AddNode(plan.PlanId, "cap.b");
        var c = AddNode(plan.PlanId, "cap.c");
        _planService.AddEdge(plan.PlanId, a.NodeId, b.NodeId, EdgeType.ControlFlow, null, null);
        _planService.AddEdge(plan.PlanId, b.NodeId, c.NodeId, EdgeType.ControlFlow, null, null);

        var result = await CreateEngine().SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        result.State.Should().Be(PlanState.Failed);
        NodeStateOf(plan.PlanId, a.NodeId).Should().Be(NodeState.Failed);
        NodeStateOf(plan.PlanId, b.NodeId).Should().Be(NodeState.Cancelled);
        NodeStateOf(plan.PlanId, c.NodeId).Should().Be(NodeState.Cancelled); // 遞移級聯
    }

    [Fact]
    public async Task SubmitAndExecute_NodeFailsWithRetries_EventuallyExhaustsAndFails()
    {
        // maxRetries=2 → 第一次失敗回 Pending 重試、第二次失敗用盡 → Failed
        BrokerStateFor("cap.a", ExecutionState.Failed, reason: "boom");
        var plan = NewDraftPlan();
        var a = AddNode(plan.PlanId, "cap.a", maxRetries: 2);

        var result = await CreateEngine().SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        result.State.Should().Be(PlanState.Failed);
        NodeStateOf(plan.PlanId, a.NodeId).Should().Be(NodeState.Failed);
        // 至少被派工 2 次（首次 + 1 次重試）
        await _broker.Received(2).SubmitExecutionRequestAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            "cap.a", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task SubmitAndExecute_UnexpectedExecutionState_TreatedAsFailure()
    {
        // 中間態（如 Dispatched）非 Succeeded/Denied/Failed → 引擎視為失敗
        BrokerStateFor("cap.a", ExecutionState.Dispatched);
        var plan = NewDraftPlan();
        var a = AddNode(plan.PlanId, "cap.a");

        var result = await CreateEngine().SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        result.State.Should().Be(PlanState.Failed);
        NodeStateOf(plan.PlanId, a.NodeId).Should().Be(NodeState.Failed);
    }

    [Fact]
    public async Task SubmitAndExecute_BrokerThrows_NodeFailedPlanFailed()
    {
        _broker.SubmitExecutionRequestAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string>())
            .Returns<Task<ExecutionRequest>>(_ => throw new InvalidOperationException("upstream down"));

        var plan = NewDraftPlan();
        var a = AddNode(plan.PlanId, "cap.a");

        var result = await CreateEngine().SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        result.State.Should().Be(PlanState.Failed);
        NodeStateOf(plan.PlanId, a.NodeId).Should().Be(NodeState.Failed);
    }

    // ─────────────────────────────────────────────────────────
    // DataFlow 寫出
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitAndExecute_NodeWithOutputKey_WritesResultToSharedContext()
    {
        BrokerAlwaysSucceeds();
        var plan = NewDraftPlan();
        AddNode(plan.PlanId, "cap.a", outputKey: "out_a");

        await CreateEngine().SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        // 有 OutputContextKey 的成功節點應把結果寫入 SharedContext
        _context.Received(1).Write(
            "p_owner",
            Arg.Any<string>(),
            "out_a",
            Arg.Any<string>(),
            "application/json",
            Arg.Any<string>(),
            plan.TaskId);
    }

    [Fact]
    public async Task SubmitAndExecute_NodeWithoutOutputKey_DoesNotWriteContext()
    {
        BrokerAlwaysSucceeds();
        var plan = NewDraftPlan();
        AddNode(plan.PlanId, "cap.a", outputKey: null);

        await CreateEngine().SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        _context.DidNotReceive().Write(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
    }

    // ─────────────────────────────────────────────────────────
    // 防呆 / 邊界
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitAndExecute_PlanNotFound_Throws()
    {
        var act = async () => await CreateEngine()
            .SubmitAndExecuteAsync("plan_missing", "p_owner", "ses_1", "trace_1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task SubmitAndExecute_PlanAlreadyCompleted_Throws()
    {
        var plan = NewDraftPlan();
        AddNode(plan.PlanId, "cap.a");
        _planService.UpdatePlanState(plan.PlanId, PlanState.Completed);

        var act = async () => await CreateEngine()
            .SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expected Draft or Submitted*");
    }

    [Fact]
    public async Task SubmitAndExecute_EmptyPlan_DagValidationFails_Throws()
    {
        var plan = NewDraftPlan(); // 無節點

        var act = async () => await CreateEngine()
            .SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*DAG validation failed*");
    }

    [Fact]
    public async Task SubmitAndExecute_CyclicGraph_DagValidationFails_Throws()
    {
        var plan = NewDraftPlan();
        var a = AddNode(plan.PlanId, "cap.a");
        var b = AddNode(plan.PlanId, "cap.b");
        // A → B → A 形成環
        _planService.AddEdge(plan.PlanId, a.NodeId, b.NodeId, EdgeType.ControlFlow, null, null);
        _planService.AddEdge(plan.PlanId, b.NodeId, a.NodeId, EdgeType.ControlFlow, null, null);

        var act = async () => await CreateEngine()
            .SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Cycle detected*");
    }

    // ─────────────────────────────────────────────────────────
    // 取消權杖（M-8）
    // ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SubmitAndExecute_PreCancelledToken_PlanCancelledAndThrows()
    {
        BrokerAlwaysSucceeds();
        var plan = NewDraftPlan();
        AddNode(plan.PlanId, "cap.a");

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 進迴圈前已取消

        var act = async () => await CreateEngine()
            .SubmitAndExecuteAsync(plan.PlanId, "p_owner", "ses_1", "trace_1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        PlanStateOf(plan.PlanId).Should().Be(PlanState.Cancelled);
    }
}
