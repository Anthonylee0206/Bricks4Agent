using System.Text.Json;
using Broker.Helpers;
using Broker.Services;
using BrokerCore.Services;

namespace Broker.Endpoints;

/// <summary>
/// 使用者審批端點(§18.2-C2)—— 經簽章連結 token 認證(非 localhost、非管理員)。
/// token 綁單一 userId;broker 再強制 owner 授權(isAdmin:false → 只能批自己的 User 層)。
/// </summary>
public static class UserApprovalEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var userApprovals = group.MapGroup("/user/approvals");

        userApprovals.MapGet("", (HttpContext ctx, ApprovalLinkService link, IBrokerService broker) =>
        {
            var userId = link.Validate(ctx.Request.Query["token"].ToString(), DateTimeOffset.UtcNow);
            if (string.IsNullOrEmpty(userId))
                return Results.Json(ApiResponseHelper.Error("Invalid or expired link.", 401), statusCode: 401);

            var items = broker.ListPendingApprovalDetailsForApprover(userId, isAdmin: false);
            return Results.Ok(ApiResponseHelper.Success(new { user = userId, total = items.Count, items }));
        }).WithBrokerAuthPolicy(BrokerAuthPolicy.SignedLink); // 由連結 token 驗證

        userApprovals.MapPost("/{approvalId}/approve", async (HttpContext ctx, ApprovalLinkService link, IBrokerService broker, string approvalId) =>
        {
            var body = RequestBodyHelper.GetBody(ctx);
            var userId = link.Validate(GetToken(ctx, body), DateTimeOffset.UtcNow);
            if (string.IsNullOrEmpty(userId))
                return Results.Json(ApiResponseHelper.Error("Invalid or expired link.", 401), statusCode: 401);

            var reason = body.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            var result = await broker.ApproveExecutionAsync(approvalId, userId, reason, isAdmin: false);
            return result == null
                ? Results.NotFound(ApiResponseHelper.Error("Approval not found, already decided, or not yours.", 404))
                : Results.Ok(ApiResponseHelper.Success(new { item = result }));
        }).WithBrokerAuthPolicy(BrokerAuthPolicy.SignedLink); // [修正 2026-10 AnthonyLee] 與上方 GET 相同，由連結 token 驗證（原本沒標，套用預設 ScopedToken 而被擋）

        userApprovals.MapPost("/{approvalId}/reject", (HttpContext ctx, ApprovalLinkService link, IBrokerService broker, string approvalId) =>
        {
            var body = RequestBodyHelper.GetBody(ctx);
            var userId = link.Validate(GetToken(ctx, body), DateTimeOffset.UtcNow);
            if (string.IsNullOrEmpty(userId))
                return Results.Json(ApiResponseHelper.Error("Invalid or expired link.", 401), statusCode: 401);

            var reason = body.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;
            var result = broker.RejectExecution(approvalId, userId, reason, isAdmin: false);
            return result == null
                ? Results.NotFound(ApiResponseHelper.Error("Approval not found, already decided, or not yours.", 404))
                : Results.Ok(ApiResponseHelper.Success(new { item = result }));
        }).WithBrokerAuthPolicy(BrokerAuthPolicy.SignedLink); // [修正 2026-10 AnthonyLee] 與上方 GET 相同，由連結 token 驗證（原本沒標，套用預設 ScopedToken 而被擋）
    }

    private static string GetToken(HttpContext ctx, JsonElement body)
        => body.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty
            : ctx.Request.Query["token"].ToString();
}
