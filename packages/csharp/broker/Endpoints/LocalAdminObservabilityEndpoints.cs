using Broker.Helpers;
using Broker.Services;
using Broker.Services.Observability;
using BrokerCore.Services;
using FunctionPool.Models;
using FunctionPool.Registry;

namespace Broker.Endpoints;

/// <summary>
/// 管理後台用的觀測 API：健康分數、worker 狀態、LLM 呼叫統計、異常偵測報告。
///
/// 為什麼要另外一組：組長的管理後台（line-admin.html）只呼叫 /api/v1/local-admin/*，
/// 用 local-admin 的 cookie 工作階段驗證、送明文 JSON；而 /api/v1/health/* 與 /api/v1/observability/*
/// 要求加密工作階段發的 scoped token，後台拿不到，所以叫不出來。這裡照 local-admin 的規格提供同一份資料：
///   - 驗證政策 LocalAdminSession（不用 scoped token），由下方的群組 filter 以 LocalAdminAuthService 驗證 cookie；
///   - 權限 system.monitor.read（SuperAdmin、SystemAdmin 與唯讀角色有，PermissionAdmin 沒有）；
///   - 明文 JSON：EncryptionMiddleware 本來就把 /api/v1/local-admin/ 列為明文路徑。
///
/// Function Pool 相關服務在 pool 關閉時不會註冊，所以這裡在 handler 內以 GetService 取用，
/// 端點本身不必跟著 poolEnabled 開關（避免 Minimal API 把沒註冊的服務參數推斷成 body）。
/// </summary>
public static class LocalAdminObservabilityEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var observability = group.MapGroup("/local-admin/observability")
            .WithBrokerAuthPolicy(BrokerAuthPolicy.LocalAdminSession);
        observability.AddEndpointFilter(async (filterContext, next) =>
        {
            var ctx = filterContext.HttpContext;
            var auth = ctx.RequestServices.GetRequiredService<LocalAdminAuthService>();
            if (!auth.TryRequirePermission(ctx, LocalAdminPermissions.SystemMonitorRead, out _, out var denied))
                return denied;
            return await next(filterContext);
        });

        // GET /api/v1/local-admin/observability/health-score — Worker 健康綜合分數
        observability.MapGet("/health-score", async (HttpContext ctx, CancellationToken ct) =>
            ctx.RequestServices.GetService<HealthScoreService>() is { } svc
                ? Results.Ok(ApiResponseHelper.Success(await HealthCheckEndpoints.BuildScorePayloadAsync(svc, ct)))
                : PoolDisabled());

        // GET /api/v1/local-admin/observability/health-score/history?since_minutes=360
        observability.MapGet("/health-score/history", (HttpContext ctx) =>
            ctx.RequestServices.GetService<HealthScoreSnapshotService>() is { } snapshots
                ? Results.Ok(ApiResponseHelper.Success(HealthCheckEndpoints.BuildScoreHistoryPayload(snapshots, ctx.Request.Query)))
                : PoolDisabled());

        // GET /api/v1/local-admin/observability/workers — registry 裡每個 worker 的狀態
        observability.MapGet("/workers", (HttpContext ctx) =>
            ctx.RequestServices.GetService<IWorkerRegistry>() is { } registry
            && ctx.RequestServices.GetService<PoolConfig>() is { } poolConfig
                ? Results.Ok(ApiResponseHelper.Success(HealthCheckEndpoints.BuildWorkersPayload(registry, poolConfig)))
                : PoolDisabled());

        // GET /api/v1/local-admin/observability/llm — LLM 代理呼叫統計
        observability.MapGet("/llm", (HttpContext ctx, LlmProxyMetrics metrics) =>
            Results.Ok(ApiResponseHelper.Success(HealthCheckEndpoints.BuildLlmPayload(metrics, ctx.Request.Query))));

        // GET /api/v1/local-admin/observability/incident-reports?limit=20
        observability.MapGet("/incident-reports", (HttpContext ctx, IncidentReportStore store) =>
            Results.Ok(ApiResponseHelper.Success(IncidentReportEndpoints.BuildListPayload(store, ctx.Request.Query))));

        // GET /api/v1/local-admin/observability/incident-reports/{reportId}
        observability.MapGet("/incident-reports/{reportId}", (string reportId, IncidentReportStore store) =>
            IncidentReportEndpoints.DetailResult(store, reportId));

        // POST /api/v1/local-admin/observability/incident-reports/run  { minutes? }
        observability.MapPost("/incident-reports/run", async (
            HttpContext ctx, IncidentReportService service, IncidentReportOptions options, CancellationToken ct) =>
            Results.Ok(ApiResponseHelper.Success(
                await IncidentReportEndpoints.RunManualAsync(service, options, RequestBodyHelper.GetBody(ctx), ct))));
    }

    /// <summary>
    /// Function Pool 沒開是設定狀態，不是伺服器故障，所以回 200 並標明 pool_enabled=false。
    /// 原本回 503：observability.html 每次載入都會讀健康分數，R6（伺服器錯誤）就把它當成異常，
    /// 等於觀測頁自己製造誤報。
    /// </summary>
    private static IResult PoolDisabled()
        => Results.Ok(ApiResponseHelper.Success(new
        {
            pool_enabled = false,
            message = "Function Pool 未啟用，沒有 worker 健康資料。",
        }));
}
