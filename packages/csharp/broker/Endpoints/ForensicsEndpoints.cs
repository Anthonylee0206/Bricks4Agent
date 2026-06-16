using System.Text.Json;
using System.Text.Json.Nodes;
using Broker.Helpers;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;

namespace Broker.Endpoints;

/// <summary>
/// Forensics endpoint —— 把同一條 trace / 同一個時間窗 / 同一個 symbol 相關的所有
/// audit_events + trading_approval_requests + llm_reasoning_audit 拉出來、按 ts 排序回傳。
///
/// 兩個對外端點：
///   GET  /api/v1/forensics/timeline   — 純資料聚合、JSON 回（dashboard 表格用）
///   POST /api/v1/forensics/investigate — 同樣聚合 + 走 LlmProxy 組敘述（demo 用、走完整 audit pipeline）
///
/// 安全：要 admin 或自己的 principal_id；非 admin 看到非自己的事件會被擋
/// （沿用 audit endpoint 同一套規則）
/// </summary>
public static class ForensicsEndpoints
{
    /// <summary>Timeline 條目（internal、給 ForensicsAgentService 共用）</summary>
    internal record TimelineEvent(
        DateTime Ts,
        string Type,
        string? TraceId,
        string? PrincipalId,
        string Summary,
        object Raw);

    /// <summary>給 ForensicsAgentService 用的純資料聚合（不需要 HttpContext）</summary>
    internal static (List<TimelineEvent> Events, TimelineSummary Summary, object Query)
        BuildTimelineCore(BrokerDb db, DateTime since, DateTime until,
            string? traceId, string? symbol, int limit,
            string? callerPrincipalId, bool isAdmin)
    {
        // 直接傳 DateTime、讓 BaseOrm 處理 SQLite TEXT 表示——避免 .ToString("o")
        // 在不同環境（test in-memory vs production）產出不同字串、BETWEEN 失配的問題
        var symbolLike = symbol != null ? $"%{symbol}%" : null;

        var auditSql = "SELECT * FROM audit_events WHERE occurred_at BETWEEN @since AND @until";
        if (!string.IsNullOrEmpty(traceId)) auditSql += " AND trace_id = @traceId";
        if (!string.IsNullOrEmpty(symbol))  auditSql += " AND (COALESCE(resource_ref,'') LIKE @symbolLike OR details LIKE @symbolLike)";
        if (!isAdmin)                       auditSql += " AND principal_id = @caller";
        auditSql += " ORDER BY occurred_at DESC LIMIT @limit";
        var auditEvents = db.Query<AuditEvent>(auditSql,
            new { since, until, traceId, symbolLike, caller = callerPrincipalId, limit });

        var aprSql = "SELECT * FROM trading_approval_requests WHERE requested_at BETWEEN @since AND @until";
        if (!string.IsNullOrEmpty(traceId)) aprSql += " AND trace_id = @traceId";
        if (!string.IsNullOrEmpty(symbol))  aprSql += " AND (route LIKE @symbolLike OR payload LIKE @symbolLike)";
        if (!isAdmin)                       aprSql += " AND principal_id = @caller";
        aprSql += " ORDER BY requested_at DESC LIMIT @limit";
        var approvals = db.Query<TradingApprovalRequest>(aprSql,
            new { since, until, traceId, symbolLike, caller = callerPrincipalId, limit });

        var llmSql = "SELECT * FROM llm_reasoning_audit WHERE occurred_at BETWEEN @since AND @until";
        if (!string.IsNullOrEmpty(symbol))  llmSql += " AND (tool_args LIKE @symbolLike OR llm_reasoning LIKE @symbolLike)";
        if (!isAdmin)                       llmSql += " AND user_id = @caller";
        llmSql += " ORDER BY occurred_at DESC LIMIT @limit";
        var llmReasoning = db.Query<LlmReasoningAuditEntry>(llmSql,
            new { since, until, symbolLike, caller = callerPrincipalId, limit });

        var events = new List<TimelineEvent>();
        foreach (var e in auditEvents)
        {
            events.Add(new TimelineEvent(
                e.OccurredAt, "audit", e.TraceId, e.PrincipalId,
                $"{e.EventType}{(string.IsNullOrEmpty(e.ResourceRef) ? "" : " · " + e.ResourceRef)}",
                e));
        }
        foreach (var a in approvals)
        {
            events.Add(new TimelineEvent(
                a.RequestedAt, "approval_requested", a.TraceId, a.PrincipalId,
                $"approval requested: {a.CapabilityId} · {a.Route}",
                new { a.ApprovalId, a.Status, a.CapabilityId, a.Route, payload_brief = TrimPayload(a.Payload) }));
            if (a.DecidedAt.HasValue)
            {
                events.Add(new TimelineEvent(
                    a.DecidedAt.Value, $"approval_{a.Status}", a.TraceId, a.DecidedBy,
                    $"approval {a.Status} by {a.DecidedBy ?? "(unknown)"}: {a.DecisionReason ?? "(no reason)"}",
                    new { a.ApprovalId, a.Status, a.DecidedBy, a.DecisionReason }));
            }
            if (a.DispatchedAt.HasValue)
            {
                events.Add(new TimelineEvent(
                    a.DispatchedAt.Value, "approval_dispatched", a.TraceId, a.DispatchedBy,
                    $"dispatched to worker by {a.DispatchedBy ?? "(unknown)"}",
                    new { a.ApprovalId, a.DispatchedBy }));
            }
        }
        foreach (var lr in llmReasoning)
        {
            events.Add(new TimelineEvent(
                lr.OccurredAt, "llm_reasoning", null, lr.UserId,
                $"[{lr.Source}] LLM → {lr.ToolName} (acl: {(lr.AclAllowed ? "allow" : "block")}, dispatch: {lr.DispatchResult})",
                lr));
        }

        var sorted = events.OrderByDescending(e => e.Ts).Take(limit).ToList();
        var typesAgg = sorted.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());

        var summary = new TimelineSummary
        {
            Types = typesAgg,
            UniqueTraces = sorted.Select(e => e.TraceId).Where(s => !string.IsNullOrEmpty(s)).Distinct().Count(),
            UniquePrincipals = sorted.Select(e => e.PrincipalId).Where(s => !string.IsNullOrEmpty(s)).Distinct().Count(),
            AuditCount = auditEvents.Count,
            ApprovalCount = approvals.Count,
            LlmCount = llmReasoning.Count
        };

        var queryEcho = new
        {
            since = since.ToString("o"), until = until.ToString("o"), trace_id = traceId, symbol, limit,
            scope = isAdmin ? "all" : "self"
        };
        return (sorted, summary, queryEcho);
    }

    /// <summary>給 ForensicsAgentService 用的 prompt 組裝（跟 endpoint 同一模板）</summary>
    internal static (string SystemPrompt, string UserPrompt) BuildLlmPrompts(
        List<TimelineEvent> timeline, TimelineSummary summary, string question)
    {
        var timelineLines = timeline.Take(60).Select(e =>
            $"- [{e.Ts:HH:mm:ss}] {e.Type} · {e.Summary}").ToList();
        var timelineText = string.Join("\n", timelineLines);
        var systemPrompt =
            "你是 Bricks4Agent 平台的鑑識分析助手。給你一段 broker audit 時間軸（含 audit / approval / llm_reasoning 事件）、你要：\n" +
            "1. 識別這段時間發生的主要事件鏈（先信號 → gate 檢查 → approval → dispatch）\n" +
            "2. 用繁體中文寫一段 6–10 句的敘述、按時間順序、引用具體時間戳跟事件類型\n" +
            "3. 結尾標出任何異常（gate 攔下 / approval 拒絕 / LLM 失敗 / dispatch 重試）\n" +
            "4. **不要編造事件**、只用提供的時間軸資料\n\n" +
            "輸出用 markdown bullet list、結尾另開一段「⚠️ 觀察到的異常」（若無就寫「無」）。";
        var userPrompt =
            $"問題: {question}\n\n" +
            $"時間軸事件（共 {timeline.Count} 筆、顯示前 60 筆）:\n{timelineText}\n\n" +
            $"統計: audit={summary.AuditCount}, approvals={summary.ApprovalCount}, llm_reasoning={summary.LlmCount}, " +
            $"unique_traces={summary.UniqueTraces}";
        return (systemPrompt, userPrompt);
    }

    public static void Map(RouteGroupBuilder group)
    {
        var forensics = group.MapGroup("/forensics");

        // GET /api/v1/forensics/timeline?since=ISO&until=ISO&trace_id=X&symbol=BTC-USDT&limit=N
        forensics.MapGet("/timeline", (HttpContext ctx, BrokerDb db, HttpRequest req) =>
        {
            var (timeline, summary, query) = BuildTimeline(ctx, db, req.Query);
            return Results.Ok(ApiResponseHelper.Success(new
            {
                query,
                count = timeline.Count,
                events = timeline.Select(e => new
                {
                    ts = e.Ts, type = e.Type, trace_id = e.TraceId, principal_id = e.PrincipalId,
                    summary = e.Summary, raw = e.Raw
                }),
                summary
            }));
        });

        // POST /api/v1/forensics/investigate
        //   { since, until, trace_id?, symbol?, limit?, question? }
        //   1. 同 timeline 聚合一份
        //   2. 把 timeline + question 餵 LlmProxy （走 MeteredLlmProxyService、進 /llm-proxy/recent ring）
        //   3. 回 { query, count, events, summary, narrative, llm_model, prompt_used }
        //
        // 為什麼這條走 broker LlmProxy 而不是直接打 Anthropic：這是平台 Benson-compliance 的核心——
        // 任何 subordinate LLM 必須走 broker 集中代理稽核。Forensics agent 自己也不能例外。
        forensics.MapPost("/investigate", async (
            HttpContext ctx, BrokerDb db, ILlmProxyService llm, CancellationToken ct) =>
        {
            if (!llm.IsEnabled)
                return Results.Json(ApiResponseHelper.Error("LlmProxy disabled, can't compose narrative", 503), statusCode: 503);

            JsonElement body;
            try { body = RequestBodyHelper.GetBody(ctx); }
            catch (Exception ex) { return Results.BadRequest(ApiResponseHelper.Error("Invalid body: " + ex.Message)); }

            // 解析 body 並轉成 query collection 重用 BuildTimeline
            var queryDict = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>();
            void SetIfPresent(string key, string bodyKey)
            {
                if (body.TryGetProperty(bodyKey, out var v) && v.ValueKind == JsonValueKind.String)
                    queryDict[key] = v.GetString()!;
            }
            SetIfPresent("since", "since");
            SetIfPresent("until", "until");
            SetIfPresent("trace_id", "trace_id");
            SetIfPresent("symbol", "symbol");
            if (body.TryGetProperty("limit", out var lim) && lim.ValueKind == JsonValueKind.Number)
                queryDict["limit"] = lim.GetInt32().ToString();
            var question = body.TryGetProperty("question", out var q) && q.ValueKind == JsonValueKind.String
                ? q.GetString() : "請組成這段時間的事件敘述、按時間順序解釋發生了什麼、有哪些 gate 觸發、是否有異常。";

            var queryCollection = new Microsoft.AspNetCore.Http.QueryCollection(
                queryDict.ToDictionary(kv => kv.Key, kv => kv.Value));
            var (timeline, statSummary, queryEcho) = BuildTimeline(ctx, db, queryCollection);

            var (systemPrompt, userPrompt) = BuildLlmPrompts(timeline, statSummary, question ?? "");

            // 走 OpenAI-compatible body 餵 LlmProxy。LlmProxyService.ChatAsync 接 JsonElement、
            // 直接走 MeteredLlmProxyService 包一層 → 進 ring buffer + LLM 全景表
            var llmBody = JsonSerializer.SerializeToElement(new
            {
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userPrompt }
                },
                task_id = "forensics_" + DateTime.UtcNow.Ticks,
                task_type = "forensics_investigation"
            });

            string narrative;
            string model;
            long durationMs;
            try
            {
                var result = await llm.ChatAsync(llmBody, null, ct);
                narrative = result.Content;
                model = result.Model;
                durationMs = result.TotalDuration;
            }
            catch (Exception ex)
            {
                return Results.Ok(ApiResponseHelper.Success(new
                {
                    query = queryEcho,
                    count = timeline.Count,
                    events = timeline.Select(e => new { ts = e.Ts, type = e.Type, summary = e.Summary }),
                    summary = statSummary,
                    narrative = (string?)null,
                    narrative_error = "LLM 失敗：" + ex.Message,
                    prompt_used = new { system = systemPrompt, user = userPrompt }
                }));
            }

            return Results.Ok(ApiResponseHelper.Success(new
            {
                query = queryEcho,
                count = timeline.Count,
                events = timeline.Select(e => new
                {
                    ts = e.Ts, type = e.Type, trace_id = e.TraceId, principal_id = e.PrincipalId,
                    summary = e.Summary
                }),
                summary = statSummary,
                narrative,
                llm_model = model,
                llm_duration_ms = durationMs,
                prompt_used = new { system = systemPrompt, user = userPrompt }
            }));
        });
    }

    /// <summary>從 HttpContext 抽出 caller 後 delegate 到 BuildTimelineCore</summary>
    private static (List<TimelineEvent> Events, TimelineSummary Summary, object Query) BuildTimeline(
        HttpContext ctx, BrokerDb db, IQueryCollection query)
    {
        var callerPrincipalId = RequestBodyHelper.GetPrincipalId(ctx);
        var isAdmin = RequestBodyHelper.IsAdmin(ctx);

        var since = query.TryGetValue("since", out var sn) && DateTime.TryParse(sn.ToString(), out var snDt)
            ? snDt.ToUniversalTime() : DateTime.UtcNow.AddHours(-1);
        var until = query.TryGetValue("until", out var un) && DateTime.TryParse(un.ToString(), out var unDt)
            ? unDt.ToUniversalTime() : DateTime.UtcNow;
        var traceId = query.TryGetValue("trace_id", out var t) ? t.ToString() : null;
        var symbol  = query.TryGetValue("symbol",   out var s) ? s.ToString() : null;
        var limit   = query.TryGetValue("limit", out var l) && int.TryParse(l, out var lv)
            ? Math.Min(lv, 500) : 200;

        return BuildTimelineCore(db, since, until, traceId, symbol, limit, callerPrincipalId, isAdmin);
    }

    /// <summary>截短 payload JSON 字串、避免時間軸太肥</summary>
    private static string TrimPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload)) return "";
        return payload.Length > 240 ? payload[..240] + "…" : payload;
    }

    public class TimelineSummary
    {
        public Dictionary<string, int> Types { get; set; } = new();
        public int UniqueTraces { get; set; }
        public int UniquePrincipals { get; set; }
        public int AuditCount { get; set; }
        public int ApprovalCount { get; set; }
        public int LlmCount { get; set; }
    }
}
