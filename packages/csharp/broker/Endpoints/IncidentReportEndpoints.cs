using System.Text.Json;
using Broker.Helpers;
using Broker.Services.Observability;

namespace Broker.Endpoints;

/// <summary>
/// 異常偵測報告的 API（scoped token、只限管理員）。
///
/// /api/v1 預設要求 scoped token，這個群組再加上 RequireBrokerAdmin()：只有管理員能查報告或手動觸發。
/// 管理後台（line-admin 的 cookie 工作階段）走 <see cref="LocalAdminObservabilityEndpoints"/>，回傳同一份資料。
/// 報告內容只有計數、ID 與規則說明，不含使用者對話內容。
/// </summary>
public static class IncidentReportEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var observability = group.MapGroup("/observability").RequireBrokerAdmin();

        // GET /api/v1/observability/incident-reports?limit=20 — 最新的報告摘要，新的在前
        observability.MapGet("/incident-reports", (HttpContext ctx, IncidentReportStore store) =>
            Results.Ok(ApiResponseHelper.Success(BuildListPayload(store, ctx.Request.Query))));

        // GET /api/v1/observability/incident-reports/{reportId} — 單份完整報告（規則結果＋涵蓋範圍＋盲點＋敘述）
        observability.MapGet("/incident-reports/{reportId}", (string reportId, IncidentReportStore store) =>
            DetailResult(store, reportId));

        // POST /api/v1/observability/incident-reports/run  { minutes? } — 立刻產生最近 N 分鐘的報告
        observability.MapPost("/incident-reports/run", async (
            HttpContext ctx, IncidentReportService service, IncidentReportOptions options, CancellationToken ct) =>
            Results.Ok(ApiResponseHelper.Success(await RunManualAsync(service, options, RequestBodyHelper.GetBody(ctx), ct))));
    }

    // ── 兩組端點共用 ──

    public static object BuildListPayload(IncidentReportStore store, IQueryCollection query)
    {
        var limit = int.TryParse(query["limit"].ToString(), out var l) ? Math.Clamp(l, 1, 100) : 20;
        var items = store.GetRecent(limit).Select(r => new
        {
            report_id = r.ReportId,
            trigger = r.Trigger,
            window_start = r.WindowStart,
            window_end = r.WindowEnd,
            created_at = r.CreatedAt,
            max_severity = r.MaxSeverity,
            finding_count = r.Findings.Count,
            rules = r.Findings.Select(f => f.RuleId).Distinct().ToList(),
            narrative_status = r.NarrativeStatus,
        }).ToList();
        return new { count = items.Count, items };
    }

    public static IResult DetailResult(IncidentReportStore store, string reportId)
    {
        var report = store.Get(reportId);
        return report is null
            ? Results.Json(ApiResponseHelper.Error("Incident report not found.", 404), statusCode: 404)
            : Results.Ok(ApiResponseHelper.Success(report));
    }

    /// <summary>產生最近 N 分鐘（body 的 minutes，預設為排程間隔，1–1440）的報告。</summary>
    public static Task<IncidentReport> RunManualAsync(
        IncidentReportService service, IncidentReportOptions options, JsonElement body, CancellationToken ct)
    {
        var minutes = body.ValueKind == JsonValueKind.Object
                      && body.TryGetProperty("minutes", out var m) && m.TryGetInt32(out var requested)
            ? requested
            : options.IntervalMinutes;
        minutes = Math.Clamp(minutes, 1, 24 * 60);

        var end = DateTime.UtcNow;
        return service.RunAsync(end.AddMinutes(-minutes), end, IncidentReportTriggers.Manual, ct);
    }
}
