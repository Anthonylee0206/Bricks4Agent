using System.Text.Json;
using Broker.Helpers;
using Broker.Middleware;
using BrokerCore.Crypto;
using BrokerCore.Data;
using BrokerCore.Models;
using BrokerCore.Services;

namespace Broker.Endpoints;

/// <summary>POST /api/v1/admin/* — 需 role_admin 授權</summary>
public static class AdminEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var admin = group.MapGroup("/admin");

        // Kill Switch：epoch 遞增 → 所有舊 token 即時失效
        admin.MapPost("/kill-switch", (HttpContext ctx, IRevocationService revocationService) =>
        {
            if (!RequireAdmin(ctx, out var denied)) return denied;

            var body = RequestBodyHelper.GetBody(ctx);
            var principalId = RequestBodyHelper.GetPrincipalId(ctx);
            var reason = body.TryGetProperty("reason", out var r)
                ? r.GetString() ?? "" : "Kill switch activated";

            var newEpoch = revocationService.IncrementEpoch(principalId, reason);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                new_epoch = newEpoch,
                message = "All tokens issued before this epoch are now invalid."
            }));
        });

        // 撤權
        admin.MapPost("/revoke", (HttpContext ctx,
            IRevocationService revocationService,
            ISessionService sessionService,
            ISessionKeyStore keyStore) =>
        {
            if (!RequireAdmin(ctx, out var denied)) return denied;

            var body = RequestBodyHelper.GetBody(ctx);
            var principalId = RequestBodyHelper.GetPrincipalId(ctx);
            var targetType = body.GetProperty("target_type").GetString() ?? "";
            var targetId = body.GetProperty("target_id").GetString() ?? "";
            var reason = body.TryGetProperty("reason", out var r)
                ? r.GetString() ?? "" : "Revoked by admin";

            var revType = targetType.ToLowerInvariant() switch
            {
                "session" => RevocationTargetType.Session,
                "grant" => RevocationTargetType.Grant,
                "token" => RevocationTargetType.Token,
                _ => throw new ArgumentException($"Unknown target_type: {targetType}")
            };

            var revocation = revocationService.Revoke(revType, targetId, reason, principalId);

            // 若撤銷 session，同時清除金鑰
            if (revType == RevocationTargetType.Session)
            {
                keyStore.Remove(targetId);
                sessionService.RevokeSession(targetId, reason, principalId);
            }

            return Results.Ok(ApiResponseHelper.Success(revocation));
        });

        // 註冊主體
        admin.MapPost("/principals/create", (HttpContext ctx, BrokerDb db) =>
        {
            if (!RequireAdmin(ctx, out var denied)) return denied;

            var body = RequestBodyHelper.GetBody(ctx);
            var actorType = body.GetProperty("actor_type").GetString() ?? "AI";
            var displayName = body.GetProperty("display_name").GetString() ?? "";
            var publicKey = body.TryGetProperty("public_key", out var pk) ? pk.GetString() : null;

            var principal = new Principal
            {
                PrincipalId = BrokerCore.IdGen.New("prn"),
                ActorType = Enum.Parse<ActorType>(actorType, true),
                DisplayName = displayName,
                PublicKey = publicKey,
                Status = EntityStatus.Active,
                CreatedAt = DateTime.UtcNow
            };

            db.Insert(principal);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                principal_id = principal.PrincipalId,
                actor_type = principal.ActorType.ToString(),
                display_name = principal.DisplayName
            }));
        });

        // 定義角色
        admin.MapPost("/roles/create", (HttpContext ctx, BrokerDb db) =>
        {
            if (!RequireAdmin(ctx, out var denied)) return denied;

            var body = RequestBodyHelper.GetBody(ctx);
            var displayName = body.GetProperty("display_name").GetString() ?? "";
            var allowedTaskTypes = body.TryGetProperty("allowed_task_types", out var att)
                ? att.GetRawText() : "[]";
            var defaultCapIds = body.TryGetProperty("default_capability_ids", out var dci)
                ? dci.GetRawText() : "[]";

            var role = new Role
            {
                RoleId = BrokerCore.IdGen.New("role"),
                DisplayName = displayName,
                AllowedTaskTypes = allowedTaskTypes,
                DefaultCapabilityIds = defaultCapIds,
                Version = 1,
                Status = EntityStatus.Active
            };

            db.Insert(role);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                role_id = role.RoleId,
                display_name = role.DisplayName
            }));
        });

        // 查詢 epoch（允許所有已認證角色查詢，但 kill-switch/revoke/create 需 admin）
        admin.MapPost("/epoch/query", (HttpContext ctx, IRevocationService revocationService) =>
        {
            var epoch = revocationService.GetCurrentEpoch();
            return Results.Ok(ApiResponseHelper.Success(new { current_epoch = epoch }));
        });

        // ── GET /api/v1/admin/acl ──
        // dump 目前 capability ACL 規則表（給 dashboard 顯示「哪個 role 能呼叫哪些 capability」）
        admin.MapGet("/acl", (HttpContext ctx, ICapabilityAclService aclService) =>
        {
            if (!RequireAdmin(ctx, out var denied)) return denied;

            var rules = aclService.GetRules();
            return Results.Ok(ApiResponseHelper.Success(new
            {
                rules = rules.Select(kv => new
                {
                    role = kv.Key,
                    patterns = kv.Value,
                    is_admin_role = kv.Value.Any(p => p == "*"),
                }),
                policy = "fail-open: empty role / unknown role → allow; only known non-admin roles enforce whitelist.",
            }));
        });

        // ── Per-principal capability overrides（個別 user 例外規則）──
        admin.MapGet("/acl/overrides", (HttpContext ctx, ICapabilityAclService aclSvc) =>
        {
            if (!RequireAdmin(ctx, out var denied)) return denied;
            var pid = ctx.Request.Query.TryGetValue("principal_id", out var p) ? p.ToString() : null;
            var list = aclSvc.ListOverrides(string.IsNullOrEmpty(pid) ? null : pid);
            return Results.Ok(ApiResponseHelper.Success(list.Select(o => new
            {
                override_id        = o.OverrideId,
                principal_id       = o.PrincipalId,
                capability_pattern = o.CapabilityPattern,
                action             = o.Action,
                created_at         = o.CreatedAt,
                created_by         = o.CreatedBy,
                reason             = o.Reason,
            })));
        });

        admin.MapPost("/acl/overrides", (HttpContext ctx, ICapabilityAclService aclSvc) =>
        {
            if (!RequireAdmin(ctx, out var denied)) return denied;
            var body = RequestBodyHelper.GetBody(ctx);
            var pid = body.GetProperty("principal_id").GetString() ?? "";
            var pat = body.GetProperty("capability_pattern").GetString() ?? "";
            var act = body.GetProperty("action").GetString() ?? "allow";
            var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;
            if (string.IsNullOrEmpty(pid) || string.IsNullOrEmpty(pat))
                return Results.BadRequest(ApiResponseHelper.Error("principal_id and capability_pattern required"));
            try
            {
                var ovr = aclSvc.AddOverride(pid, pat, act, RequestBodyHelper.GetPrincipalId(ctx), reason);
                return Results.Ok(ApiResponseHelper.Success(new { override_id = ovr.OverrideId, action = ovr.Action }));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ApiResponseHelper.Error(ex.Message));
            }
        });

        admin.MapPost("/acl/overrides/{id}/remove", (string id, HttpContext ctx, ICapabilityAclService aclSvc) =>
        {
            if (!RequireAdmin(ctx, out var denied)) return denied;
            var ok = aclSvc.RemoveOverride(id);
            if (!ok) return Results.BadRequest(ApiResponseHelper.Error("override_id not found"));
            return Results.Ok(ApiResponseHelper.Success(new { override_id = id, removed = true }));
        });

        // ── Approval workflow（高風險 capability 需 admin 點 approve）──
        admin.MapGet("/approvals", (HttpContext ctx, ITradingApprovalService aprSvc) =>
        {
            if (!RequireAdmin(ctx, out var denied)) return denied;
            var status = ctx.Request.Query.TryGetValue("status", out var s) ? s.ToString() : "pending";
            var limit = int.TryParse(ctx.Request.Query["limit"].ToString(), out var l) ? Math.Clamp(l, 1, 200) : 50;
            var list = aprSvc.List(string.IsNullOrEmpty(status) ? null : status, limit);
            return Results.Ok(ApiResponseHelper.Success(list.Select(a => new
            {
                approval_id     = a.ApprovalId,
                trace_id        = a.TraceId,
                capability_id   = a.CapabilityId,
                route           = a.Route,
                payload         = a.Payload,
                principal_id    = a.PrincipalId,
                role            = a.Role,
                requested_at    = a.RequestedAt,
                status          = a.Status,
                decided_by      = a.DecidedBy,
                decided_at      = a.DecidedAt,
                decision_reason = a.DecisionReason,
                dispatched_at   = a.DispatchedAt,
                dispatched_by   = a.DispatchedBy,
                // W14 P3 — admin 一眼看出風險量級
                risk_hint       = Broker.Services.ApprovalRiskHintHelper.Hint(a.CapabilityId, a.Payload),
            })));
        });

        admin.MapPost("/approvals/{id}/approve", (string id, HttpContext ctx, ITradingApprovalService aprSvc) =>
        {
            if (!RequireAdmin(ctx, out var denied)) return denied;
            var body = RequestBodyHelper.GetBody(ctx);
            var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;
            var by = RequestBodyHelper.GetPrincipalId(ctx);
            var ok = aprSvc.Approve(id, by, reason);
            if (!ok) return Results.BadRequest(ApiResponseHelper.Error("approval_id not found or already decided"));
            return Results.Ok(ApiResponseHelper.Success(new { approval_id = id, status = "approved" }));
        });

        admin.MapPost("/approvals/{id}/reject", (string id, HttpContext ctx, ITradingApprovalService aprSvc) =>
        {
            if (!RequireAdmin(ctx, out var denied)) return denied;
            var body = RequestBodyHelper.GetBody(ctx);
            var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;
            var by = RequestBodyHelper.GetPrincipalId(ctx);
            var ok = aprSvc.Reject(id, by, reason);
            if (!ok) return Results.BadRequest(ApiResponseHelper.Error("approval_id not found or already decided"));
            return Results.Ok(ApiResponseHelper.Success(new { approval_id = id, status = "rejected" }));
        });

        // ── 核准 + 立刻派發（一鍵閉環）──
        // 把「approve → caller retry」兩步合一：dispatcher 看到 status='approved' 直接放行
        // 派發時 PrincipalId 仍用原申請者、責任歸屬正確；audit 自動寫 DISPATCH_APPROVED + STARTED + SUCCEEDED
        admin.MapPost("/approvals/{id}/approve-and-dispatch", async (string id, HttpContext ctx,
            ITradingApprovalService aprSvc, BrokerCore.Services.IExecutionDispatcher dispatcher) =>
        {
            if (!RequireAdmin(ctx, out var denied)) return denied;
            var body = RequestBodyHelper.GetBody(ctx);
            var reason = body.TryGetProperty("reason", out var r) ? r.GetString() : null;
            var by = RequestBodyHelper.GetPrincipalId(ctx);

            var apr = aprSvc.Get(id);
            if (apr == null) return Results.BadRequest(ApiResponseHelper.Error("approval_id not found"));
            if (apr.Status == "rejected")
                return Results.BadRequest(ApiResponseHelper.Error("approval already rejected; cannot dispatch"));

            // 冪等鎖：已派過就不能再派——避免「立刻執行」被多按或被重複 curl、害 admin 真錢真單下兩次
            if (apr.DispatchedAt != null)
                return Results.BadRequest(ApiResponseHelper.Error(
                    $"approval already dispatched at {apr.DispatchedAt:yyyy-MM-dd HH:mm:ss} UTC by {apr.DispatchedBy ?? "?"}; refuse to re-dispatch"));

            // 還在 pending → 先 approve；若已 approved 則直接 dispatch（idempotent retry 友善）
            if (apr.Status == "pending")
            {
                if (!aprSvc.Approve(id, by, reason))
                    return Results.BadRequest(ApiResponseHelper.Error("approve failed"));
            }

            // dispatch 之**前**就 set DispatchedAt（pessimistic lock）。
            // 若 dispatch 後續失敗、admin 看「已執行」+ dispatch_error 自己判斷要不要排查、不會自動 retry。
            // 比起 dispatch 後才 set 的 race 風險（這次踩到的：第一次 click 真下單但 SQL 例外讓
            // dispatched_at 沒寫進、第二次 click 又下了一單），這個方向對真錢更安全。
            if (!aprSvc.MarkDispatched(id, by))
                return Results.BadRequest(ApiResponseHelper.Error("failed to acquire dispatch lock; another caller may be dispatching"));

            // 用原申請者的 (PrincipalId, Role, TraceId) 派發、PoolDispatcher 看到 approved 會放行
            var req = new BrokerCore.Contracts.ApprovedRequest
            {
                RequestId    = Guid.NewGuid().ToString("N"),
                CapabilityId = apr.CapabilityId,
                Route        = apr.Route,
                Payload      = apr.Payload,
                Scope        = "{}",
                PrincipalId  = apr.PrincipalId,
                TaskId       = "approval-dispatch",
                SessionId    = "approval-dispatch",
                Role         = apr.Role,
                TraceId      = apr.TraceId,   // 同 trace、Gantt 上接續
            };
            var result = await dispatcher.DispatchAsync(req);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                approval_id = id,
                status = "approved",
                dispatch_success = result.Success,
                dispatch_error = result.Success ? null : result.ErrorMessage,
                trace_id = apr.TraceId,
                dispatched_at = DateTime.UtcNow,
                result_payload = result.Success ? result.ResultPayload : null,
            }));
        });
    }

    /// <summary>
    /// 角色授權閘道：僅 role_admin 可通過
    /// </summary>
    private static bool RequireAdmin(HttpContext ctx, out IResult denied)
    {
        if (RequestBodyHelper.IsAdmin(ctx))
        {
            denied = null!;
            return true;
        }

        denied = Results.Json(
            ApiResponseHelper.Error("Forbidden: admin role required.", 403),
            statusCode: 403);
        return false;
    }
}
