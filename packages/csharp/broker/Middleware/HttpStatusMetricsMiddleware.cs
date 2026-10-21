using Broker.Services.Observability;
using Microsoft.AspNetCore.Routing;

namespace Broker.Middleware;

/// <summary>
/// 記錄每個 /api/ 回應的狀態碼（見 <see cref="HttpStatusMetrics"/>）。
///
/// 必須掛在管線最外層：稽核中介層排在加密與驗證之後，被前面擋下的請求不會進 audit_events，
/// 只有從最外層才看得到所有回應的最終狀態碼（包含例外處理轉成的 500）。
/// 只計數，不讀 body、不改回應。以路由比對到的端點範本分組（路由在管線一開始就已比對完成），
/// 不記原始路徑，所以亂打的網址不會讓統計無限長大。
/// </summary>
public sealed class HttpStatusMetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HttpStatusMetrics _metrics;

    public HttpStatusMetricsMiddleware(RequestDelegate next, HttpStatusMetrics metrics)
    {
        _next = next;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        try
        {
            await _next(context);
            _metrics.Record(RouteTemplate(context), context.Response.StatusCode, DateTime.UtcNow);
        }
        catch
        {
            _metrics.Record(RouteTemplate(context), StatusCodes.Status500InternalServerError, DateTime.UtcNow);
            throw;
        }
    }

    private static string? RouteTemplate(HttpContext context)
        => (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
}

public static class HttpStatusMetricsMiddlewareExtensions
{
    public static IApplicationBuilder UseHttpStatusMetrics(this IApplicationBuilder app)
        => app.UseMiddleware<HttpStatusMetricsMiddleware>();
}
