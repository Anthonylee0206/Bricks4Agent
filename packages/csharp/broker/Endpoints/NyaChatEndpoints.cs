using Broker.Helpers;
using Broker.Services;
using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.Endpoints;

/// <summary>
/// NyaChat V2 管理 API 端點。
/// 掛載在 /api/v1/local-admin/nya/ 下，全部需要 LocalAdmin 認證。
/// 提供記憶查詢、版本回滾、審計日誌、Soul 管理等功能。
/// </summary>
public static class NyaChatEndpoints
{
    public static void Map(RouteGroupBuilder group)
    {
        var nya = group.MapGroup("/local-admin/nya")
            .WithBrokerAuthPolicy(BrokerAuthPolicy.LocalAdminSession);
        nya.AddEndpointFilter(async (filterContext, next) =>
        {
            var ctx = filterContext.HttpContext;
            var path = ctx.Request.Path.Value ?? string.Empty;
            const string marker = "/local-admin/nya";
            var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0 || !TryResolveRouteAuthorization(
                    ctx.Request.Method,
                    path[(index + marker.Length)..],
                    out var permissions,
                    out var superAdminOnly))
            {
                return Results.Json(
                    ApiResponseHelper.Error("Forbidden: unknown NyaChat admin route.", 403),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var auth = ctx.RequestServices.GetRequiredService<LocalAdminAuthService>();
            if (superAdminOnly)
            {
                if (!auth.TryRequireAuthenticated(ctx, out var session, out var denied))
                    return denied;

                if (!string.Equals(LocalAdminRoles.Normalize(session.Role), LocalAdminRoles.SuperAdmin, StringComparison.Ordinal))
                {
                    return Results.Json(
                        ApiResponseHelper.Error("Forbidden: super_admin role required.", 403),
                        statusCode: StatusCodes.Status403Forbidden);
                }
            }
            else if (permissions.Length == 1)
            {
                if (!auth.TryRequirePermission(ctx, permissions[0], out _, out var denied))
                    return denied;
            }
            else if (!auth.TryRequireAnyPermission(ctx, permissions, out _, out var denied))
            {
                return denied;
            }

            if (!auth.TryRequireAuthenticated(ctx, out var operatorSession, out var sessionDenied))
                return sessionDenied;
            using var auditScope = ctx.RequestServices.GetRequiredService<NyaAuditLogger>()
                .BeginAdminOperation(operatorSession.Username, operatorSession.Role);
            return await next(filterContext);
        });

        // 與 GET 端點相同的空值處理（空白 = 未篩選），確保刪除範圍與列表一致。
        static string? QueryOrNull(HttpContext ctx, string key)
        {
            var v = ctx.Request.Query[key].ToString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        // ── 工具管理 ────────────────────────────────────────────────────────
        nya.MapGet("/tools", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaToolRegistry toolRegistry) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var tools = toolRegistry.GetAllWithStatus();
            return Results.Ok(ApiResponseHelper.Success(new
            {
                total = tools.Count,
                tools = tools.Select(t => new
                {
                    id                  = t.Schema.Name,
                    name                = t.Schema.Name,
                    description         = t.Schema.Description,
                    group               = t.Schema.Group,
                    required_permission = t.Schema.RequiredPermission,
                    enabled             = t.Enabled
                })
            }));
        });

        nya.MapGet("/tools/availability", (HttpContext ctx, NyaToolRegistry registry) =>
        {
            var userId = ctx.Request.Query["user_id"].ToString().Trim();
            var channel = ctx.Request.Query["channel_type"].ToString().Trim();
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(channel))
                return Results.BadRequest(ApiResponseHelper.Error("user_id 與 channel_type 為必填。"));
            var usable = registry.GetUsableSchemas(userId, channel).Select(s => s.Name).ToHashSet();
            var unavailable = registry.GetUnavailableSchemas(userId, channel).ToDictionary(t => t.Schema.Name, t => t.Reason);
            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId, channel_type = channel, usable_count = usable.Count,
                tools = registry.GetAllWithStatus().Select(t => new
                {
                    id = t.Schema.Name, enabled = t.Enabled, usable = usable.Contains(t.Schema.Name),
                    function_tool = usable.Contains(t.Schema.Name),
                    reason = unavailable.GetValueOrDefault(t.Schema.Name), parameters = t.Schema.Parameters
                })
            }));
        });

        nya.MapPost("/tools/{toolId}/toggle", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaToolRegistry toolRegistry,
            NyaAuditLogger auditLogger,
            string toolId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            if (!toolRegistry.IsKnownTool(toolId))
                return Results.NotFound(ApiResponseHelper.Error($"Unknown tool: {toolId}"));

            var body = RequestBodyHelper.GetBody(ctx);
            if (!body.TryGetProperty("enabled", out var enabledEl) ||
                enabledEl.ValueKind != JsonValueKind.True && enabledEl.ValueKind != JsonValueKind.False)
                return Results.BadRequest(ApiResponseHelper.Error("Request body must contain { \"enabled\": true|false }"));

            var enabled = enabledEl.GetBoolean();
            try { toolRegistry.SetEnabled(toolId, enabled); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                auditLogger.LogError("__sys__", "tool_toggle", ex.Message);
                return Results.Json(ApiResponseHelper.Error($"Tool toggle could not be saved: {ex.Message}", 500), statusCode: 500);
            }
            auditLogger.LogToolToggle(toolId, enabled);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                tool_id = toolId,
                enabled
            }));
        });

        // ── Prompt 模板（大項五 5A）────────────────────────────────────────────
        // 列出目前載入的模板（id + version）。模板內容外部化於 prompts/*.json，可熱重載。
        nya.MapGet("/prompts", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaPromptTemplateProvider templates) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var list = templates.ListTemplates();
            return Results.Ok(ApiResponseHelper.Success(new
            {
                total = list.Count,
                templates = list.Select(t => new { id = t.Id, version = t.Version })
            }));
        });

        // 手動觸發重載（FileSystemWatcher 已自動重載；此端點供 admin 強制刷新）。
        nya.MapPost("/prompts/reload", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaPromptTemplateProvider templates,
            NyaAuditLogger auditLogger) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            templates.Reload();
            var list = templates.ListTemplates();
            auditLogger.LogReload("prompt_reload", list.Count);
            return Results.Ok(ApiResponseHelper.Success(new
            {
                ok = true,
                loaded = list.Count,
                templates = list.Select(t => new { id = t.Id, version = t.Version })
            }));
        });

        // ── 引擎狀態 ────────────────────────────────────────────────────────
        nya.MapGet("/status", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            INyaConfigStore<NyaChatConfig> configStore,
            Broker.NyaChat.NyaLlmProfileStore store,
            NyaSoulProvider soulProvider) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var config = configStore.Current; // 大項六 6D：當前配置快照
            var souls = soulProvider.ListSouls();

            // Doc 3 Plan C：以 profile store 解析每個呼叫點「實際生效」的 profile（取代 router/legacy 合成）。
            var tasks = new[]
            {
                Broker.NyaChat.NyaLlmTasks.Chat,
                Broker.NyaChat.NyaLlmTasks.Summarization
            };
            var routing = tasks.ToDictionary(
                t => t,
                t =>
                {
                    var p = store.ResolveProfile(t);
                    return new { provider = p.Provider, model = p.Model, base_url = p.BaseUrl, timeout_seconds = p.TimeoutSeconds };
                });

            return Results.Ok(ApiResponseHelper.Success(new
            {
                enabled = config.Enabled,
                auto_summarize_enabled = config.AutoSummarizeEnabled,
                // Doc 3 Plan C：以呼叫點路由解析後的實際 model/provider 呈現
                task_routing = routing,
                profiles = store.ListProfiles().Select(e => new
                {
                    name = e.ProfileId,
                    provider = e.Provider,
                    model = e.Model,
                    template = e.Template
                }),
                // 相容鍵：沿用舊欄位名，值改取自 chat 路由結果
                chat_model = routing[Broker.NyaChat.NyaLlmTasks.Chat].model,
                chat_provider = routing[Broker.NyaChat.NyaLlmTasks.Chat].provider,
                summarization_model = routing[Broker.NyaChat.NyaLlmTasks.Summarization].model,
                short_term_message_limit = config.ShortTermMessageLimit,
                summarize_trigger_count = config.SummarizeTriggerCount,
                fact_version_retention = config.FactVersionRetention,
                summary_version_retention = config.SummaryVersionRetention,
                default_soul_id = config.DefaultSoulId,
                available_souls = souls.Select(s => new { id = s.Id, display_name = s.DisplayName })
            }));
        });

        // ── 可重載配置（大項六 6D）────────────────────────────────────────────
        nya.MapGet("/config", (HttpContext ctx, LocalAdminAuthService auth, INyaConfigStore<NyaChatConfig> configStore) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            return Results.Ok(ApiResponseHelper.Success(NyaChatHotSettings.MaskSecrets(configStore.Current)));
        });

        nya.MapGet("/config/effective", (HttpContext ctx, LocalAdminAuthService auth, INyaConfigStore<NyaChatConfig> configStore) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            return Results.Ok(ApiResponseHelper.Success(((NyaChatConfigStore)configStore).Effective()));
        });

        static async Task<IResult> PatchConfig(HttpContext ctx, LocalAdminAuthService auth, INyaConfigStore<NyaChatConfig> configStore, NyaSoulProvider soulProvider)
        {
            if (!auth.TryRequireAuthenticated(ctx, out var session, out var denied)) return denied;
            try
            {
                var patch = RequestBodyHelper.GetBody(ctx);
                if (patch.ValueKind == JsonValueKind.Object)
                    foreach (var property in patch.EnumerateObject())
                        if (property.Name.Equals("DefaultSoulId", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String &&
                            !soulProvider.ListSouls().Any(s => s.Id.Equals(property.Value.GetString(), StringComparison.OrdinalIgnoreCase)))
                            return Results.BadRequest(ApiResponseHelper.Error("DefaultSoulId 必須指向現有 Soul，請先至 Soul 人格頁建立。"));
                var changed = await ((NyaChatConfigStore)configStore).PatchAsync(patch, session.Username, session.Role, ctx.RequestAborted);
                return Results.Ok(ApiResponseHelper.Success(new { changed, config = NyaChatHotSettings.MaskSecrets(configStore.Current) }));
            }
            catch (ArgumentException ex) { return Results.BadRequest(ApiResponseHelper.Error(ex.Message)); }
        }

        nya.MapPatch("/config", PatchConfig);
        nya.MapPut("/config", PatchConfig);
        nya.MapPost("/config/reset", async (HttpContext ctx, LocalAdminAuthService auth, INyaConfigStore<NyaChatConfig> configStore) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out var session, out var denied)) return denied;
            await ((NyaChatConfigStore)configStore).ResetAsync(session.Username, session.Role, ctx.RequestAborted);
            return Results.Ok(ApiResponseHelper.Success(((NyaChatConfigStore)configStore).Effective()));
        });

        // ── NEW-1: 列出所有使用者 ─────────────────────────────────────────────────
        nya.MapGet("/users", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaSoulProvider soulProvider) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var limit   = int.TryParse(ctx.Request.Query["limit"].ToString(),  out var l) ? l : 50;
            var offset  = int.TryParse(ctx.Request.Query["offset"].ToString(), out var o) ? o : 0;
            var keyword = ctx.Request.Query["keyword"].ToString();
            if (string.IsNullOrWhiteSpace(keyword)) keyword = null;

            var (users, total) = memoryService.ListUsers(limit, offset, keyword);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                total,
                limit,
                offset,
                keyword,
                users = users.Select(u =>
                {
                    var resolved = soulProvider.ResolveSoul(u.UserId);
                    return new
                    {
                        user_id           = u.UserId,
                        message_count     = u.MsgCount,
                        active_fact_count = u.ActiveFactCount,
                        last_message_at   = u.LastAt,
                        soul_id           = resolved.Soul.SoulId,
                        bound_soul_id     = resolved.BoundSoulId,
                        effective_soul_id = resolved.Soul.SoulId,
                        soul_reason       = resolved.Reason
                    };
                })
            }));
        });

        // ── NEW-2: 使用者總覽（一次取得所有統計）────────────────────────────────
        nya.MapGet("/users/{userId}/overview", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaSoulProvider soulProvider,
            INyaConfigStore<NyaChatConfig> configStore,
            NyaAuditLogger auditLogger,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            Broker.NyaChat.NyaUserOverview ov;
            try { ov = memoryService.GetUserOverview(userId); }
            catch (Exception ex)
            {
                return Results.Json(ApiResponseHelper.Error($"GetUserOverview failed: {ex.Message}", 500), statusCode: 500);
            }

            var resolved = soulProvider.ResolveSoul(userId);
            var config = configStore.Current;
            var activeTopic = memoryService.GetActiveTopic(userId);
            var recentHistory = activeTopic == null ? new List<NyaMessage>() : memoryService.GetRecentConversation(userId, activeTopic.TopicId,
                config.ShortTermMessageLimit, includeSummarized: !config.AutoSummarizeEnabled);
            var lastInput = auth.GetStatus(ctx).Permissions.Contains(LocalAdminPermissions.AuditRead)
                ? auditLogger.GetLastChatInput(userId) : null;
            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id  = ov.UserId,
                active_topic = activeTopic == null ? null : new { id = activeTopic.TopicId, title = activeTopic.Title },
                available_souls = soulProvider.ListSouls().Select(s => new { id = s.Id, display_name = s.DisplayName }),
                forgotten_count = memoryService.GetForgotten(userId).Count,
                last_turn = lastInput == null ? null : new { trace_id = lastInput.TraceId, created_at = lastInput.CreatedAt, detail_json = lastInput.DetailJson },
                auto_summarize_enabled = config.AutoSummarizeEnabled,
                short_term_message_limit = config.ShortTermMessageLimit,
                soul_id  = resolved.Soul.SoulId,
                bound_soul_id = resolved.BoundSoulId,
                effective_soul_id = resolved.Soul.SoulId,
                soul_reason = resolved.Reason,
                messages = new
                {
                    total      = ov.Messages.Total,
                    active     = ov.Messages.Active,
                    summarized = ov.Messages.Summarized,
                    deleted    = ov.Messages.Deleted,
                    recent_history = recentHistory.Count
                },
                facts = new
                {
                    total_active = ov.Facts.TotalActive,
                    categories   = ov.Facts.Categories
                },
                summaries = new
                {
                    total_versions        = ov.Summaries.TotalVersions,
                    active_count          = ov.Summaries.ActiveSummaryCount,
                    latest_covered_to_seq = ov.Summaries.LatestCoveredToSeq
                },
                last_message_at      = ov.LastMessageAt,
                last_fact_updated_at = ov.LastFactUpdatedAt
            }));
        });

        // ── NEW-3: 單則完整訊息 ──────────────────────────────────────────────────
        nya.MapGet("/users/{userId}/messages/{messageId}", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            string userId,
            string messageId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var msg = memoryService.GetMessage(userId, messageId);
            if (msg == null)
                return Results.NotFound(ApiResponseHelper.Error("Message not found.", 404));

            return Results.Ok(ApiResponseHelper.Success(new
            {
                message_id    = msg.MessageId,
                user_id       = msg.UserId,
                role          = msg.Role,
                content       = msg.Content,
                sequence      = msg.Sequence,
                topic_id      = msg.TopicId,
                is_deleted    = msg.IsDeleted,
                is_summarized = msg.IsSummarized,
                metadata_json = msg.MetadataJson,
                created_at    = msg.CreatedAt
            }));
        });

        // ── NEW-4: 手動新增 / 覆寫事實 ──────────────────────────────────────────
        nya.MapPost("/users/{userId}/facts", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var body = RequestBodyHelper.GetBody(ctx);
            if (!RequestBodyHelper.TryGetRequired(body, "category", out var category, out var e1)) return e1!;
            if (!RequestBodyHelper.TryGetRequired(body, "key",      out var key,      out var e2)) return e2!;
            if (!RequestBodyHelper.TryGetRequired(body, "value",    out var value,    out var e3)) return e3!;

            var confidence = 1.0f;
            if (body.TryGetProperty("confidence", out var confEl) &&
                confEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                confidence = confEl.GetSingle();

            string? sourceText = null;
            if (body.TryGetProperty("source_text", out var sourceEl) && sourceEl.ValueKind != JsonValueKind.Null)
            {
                if (sourceEl.ValueKind != JsonValueKind.String)
                    return Results.BadRequest(ApiResponseHelper.Error("source_text must be a string.", 400));
                sourceText = sourceEl.GetString();
            }

            DateTime? expiresAt = null;
            if (body.TryGetProperty("expires_in_days", out var daysEl) && daysEl.ValueKind != JsonValueKind.Null)
            {
                if (daysEl.ValueKind != JsonValueKind.Number || !daysEl.TryGetInt32(out var days) || days <= 0)
                    return Results.BadRequest(ApiResponseHelper.Error("expires_in_days must be a positive integer.", 400));
                try { expiresAt = DateTime.UtcNow.AddDays(days); }
                catch (ArgumentOutOfRangeException)
                {
                    return Results.BadRequest(ApiResponseHelper.Error("expires_in_days is out of range.", 400));
                }
            }

            var fact = memoryService.UpsertFact(userId, category, key, value, null, confidence, sourceText, expiresAt);
            auditLogger.LogFactUpsert(userId, key, category, fact.Version);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id    = userId,
                fact_id    = fact.FactId,
                fact_key   = fact.FactKey,
                category   = fact.Category,
                fact_value = fact.FactValue,
                source_text = fact.SourceText,
                expires_at = fact.ExpiresAt?.ToString("O"),
                version    = fact.Version
            }));
        });

        // ── Soul 管理 ────────────────────────────────────────────────────────
        nya.MapGet("/souls", (HttpContext ctx, LocalAdminAuthService auth, NyaSoulProvider soulProvider) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var souls = soulProvider.ListSouls();
            return Results.Ok(ApiResponseHelper.Success(new
            {
                total = souls.Count,
                souls = souls.Select(s => new { id = s.Id, display_name = s.DisplayName })
            }));
        });

        nya.MapPost("/souls/reload", (HttpContext ctx, LocalAdminAuthService auth, NyaSoulProvider soulProvider, NyaAuditLogger auditLogger) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var failures = soulProvider.ReloadSouls().Select(f => new { file = f.File, message = f.Message }).ToArray();
            var souls = soulProvider.ListSouls();
            auditLogger.LogReload("soul_reload", souls.Count, failures, failures.Length == 0 ? "success" : "error");
            return Results.Ok(ApiResponseHelper.Success(new
            {
                ok = failures.Length == 0,
                loaded = souls.Count,
                failures,
                souls = souls.Select(s => new { id = s.Id, display_name = s.DisplayName })
            }));
        });

        // ── Soul CRUD ────────────────────────────────────────────────────────

        nya.MapGet("/souls/{soulId}", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaSoulProvider soulProvider,
            NyaDb nyaDb,
            string soulId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var soul = soulProvider.GetSoulDefinition(soulId);
            if (soul == null)
                return Results.NotFound(ApiResponseHelper.Error($"Soul '{soulId}' not found.", 404));

            return Results.Ok(ApiResponseHelper.Success(new
            {
                soul_id      = soul.SoulId,
                bound_users = nyaDb.Db.Query<NyaSoulBinding>(
                    "SELECT * FROM nya_soul_bindings WHERE soul_id = @soulId COLLATE NOCASE ORDER BY user_id",
                    new { soulId }).Select(b => b.UserId),
                display_name = soul.DisplayName,
                personality  = new
                {
                    tone           = soul.Personality.Tone,
                    language       = soul.Personality.Language,
                    greeting_style = soul.Personality.GreetingStyle,
                    humor_level    = soul.Personality.HumorLevel,
                    formality      = soul.Personality.Formality,
                },
                rules     = soul.Rules,
                forbidden = soul.Forbidden,
            }));
        });

        nya.MapPost("/souls", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaSoulProvider soulProvider,
            NyaAuditLogger auditLogger) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var body = RequestBodyHelper.GetBody(ctx);
            var soul = ParseSoulFromBody(body, out var err);
            if (soul == null) return err!;

            if (soulProvider.GetSoulDefinition(soul.SoulId) != null)
                return Results.Conflict(ApiResponseHelper.Error($"Soul '{soul.SoulId}' already exists. Use PUT to update."));

            soulProvider.SaveSoul(soul);
            auditLogger.LogSoulChange("soul_create", soul.SoulId);

            return Results.Ok(ApiResponseHelper.Success(new { soul_id = soul.SoulId, display_name = soul.DisplayName }));
        });

        nya.MapPut("/souls/{soulId}", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaSoulProvider soulProvider,
            NyaAuditLogger auditLogger,
            string soulId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            if (soulProvider.GetSoulDefinition(soulId) == null)
                return Results.NotFound(ApiResponseHelper.Error($"Soul '{soulId}' not found. Use POST to create.", 404));

            var body = RequestBodyHelper.GetBody(ctx);
            var soul = ParseSoulFromBody(body, out var err);
            if (soul == null) return err!;

            // Enforce soul_id consistency (path param wins)
            soul.SoulId = soulId;

            soulProvider.SaveSoul(soul);
            auditLogger.LogSoulChange("soul_update", soul.SoulId);

            return Results.Ok(ApiResponseHelper.Success(new { soul_id = soul.SoulId, display_name = soul.DisplayName }));
        });

        nya.MapDelete("/souls/{soulId}", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            INyaConfigStore<NyaChatConfig> configStore,
            NyaSoulProvider soulProvider,
            NyaAuditLogger auditLogger,
            string soulId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            if (string.Equals(soulId, configStore.Current.DefaultSoulId, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(ApiResponseHelper.Error($"Cannot delete the configured default soul '{soulId}'. Change DefaultSoulId in config first."));

            var deleted = soulProvider.DeleteSoul(soulId);
            if (!deleted)
                return Results.NotFound(ApiResponseHelper.Error($"Soul '{soulId}' not found.", 404));

            auditLogger.LogSoulChange("soul_delete", soulId);
            return Results.Ok(ApiResponseHelper.Success(new { soul_id = soulId, deleted = true }));
        });

        nya.MapPost("/users/{userId}/soul", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            NyaSoulProvider soulProvider,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var body = RequestBodyHelper.GetBody(ctx);
            if (!RequestBodyHelper.TryGetRequired(body, "soul_id", out var soulId, out var error))
                return error!;

            var available = soulProvider.ListSouls().Select(s => s.Id).ToList();
            if (!available.Contains(soulId, StringComparer.OrdinalIgnoreCase))
                return Results.BadRequest(ApiResponseHelper.Error($"Soul '{soulId}' not found. Available: {string.Join(", ", available)}"));

            memoryService.SetSoulBinding(userId, soulId);
            auditLogger.LogSoulChange("soul_bind", soulId, userId);

            return Results.Ok(ApiResponseHelper.Success(new { user_id = userId, soul_id = soulId }));
        });

        // ── 訊息記錄 ────────────────────────────────────────────────────────
        nya.MapDelete("/users/{userId}/soul", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaSoulProvider soulProvider,
            NyaAuditLogger auditLogger,
            string userId) =>
        {
            if (!auth.TryRequirePermission(ctx, LocalAdminPermissions.PermissionUserManage, out _, out var denied))
                return denied;

            var previous = memoryService.GetSoulBinding(userId);
            var removed = memoryService.RemoveSoulBinding(userId);
            auditLogger.LogSoulChange("soul_unbind", previous, userId);
            var resolved = soulProvider.ResolveSoul(userId);
            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                removed,
                bound_soul_id = resolved.BoundSoulId,
                effective_soul_id = resolved.Soul.SoulId,
                soul_reason = resolved.Reason
            }));
        });

        nya.MapGet("/users/{userId}/messages", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var limit  = int.TryParse(ctx.Request.Query["limit"].ToString(),  out var l) ? l : 50;
            var offset = int.TryParse(ctx.Request.Query["offset"].ToString(), out var o) ? o : 0;
            // 大項#5：可選話題篩選。
            var topicId = ctx.Request.Query["topic_id"].ToString();
            if (string.IsNullOrWhiteSpace(topicId)) topicId = null;
            var unassigned = bool.TryParse(ctx.Request.Query["unassigned"], out var onlyUnassigned) && onlyUnassigned;
            var messages = memoryService.GetAllMessages(userId, limit, offset, topicId, unassigned);
            var total = memoryService.CountAllMessages(userId, topicId, unassigned);
            var diagnostics = memoryService.DiagnoseMessages(userId, messages);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                total,
                limit,
                offset,
                topic_id = topicId,
                external_conversations = memoryService.GetExternalConversationIds(userId),
                messages = messages.Select(m => new
                {
                    message_id      = m.MessageId,
                    role            = m.Role,
                    content_preview = m.Content.Length > 100 ? m.Content[..100] + "…" : m.Content,
                    sequence        = m.Sequence,
                    topic_id        = m.TopicId,
                    is_deleted      = m.IsDeleted,
                    is_summarized   = m.IsSummarized,
                    metadata_json   = m.MetadataJson,
                    exclusion_reasons = diagnostics[m.MessageId],
                    created_at      = m.CreatedAt
                })
            }));
        });

        nya.MapDelete("/users/{userId}/messages", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            // 若指定 message_ids 則僅軟刪除指定訊息（最多 50 筆），否則全部刪除
            var messageIdsRaw = ctx.Request.Query["message_ids"].ToString();
            if (!string.IsNullOrWhiteSpace(messageIdsRaw))
            {
                var ids = messageIdsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => s.Length > 0)
                    .ToList();
                var count = memoryService.DeleteMessagesByIds(userId, ids);
                auditLogger.LogMemoryDelete(userId, "messages_selective", $"{count} ids");
                return Results.Ok(ApiResponseHelper.Success(new
                {
                    user_id = userId,
                    soft_deleted = count,
                    selective = true,
                    requested_ids = ids.Count
                }));
            }

            // 全刪需要明確的 confirm=all 防止誤操作
            var confirmAll = ctx.Request.Query["confirm"].ToString();
            if (!string.Equals(confirmAll, "all", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(ApiResponseHelper.Error("Mass delete requires ?confirm=all query parameter."));

            var totalCount = memoryService.DeleteAllMessages(userId);
            auditLogger.LogMemoryDelete(userId, "messages");
            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                soft_deleted = totalCount,
                selective = false
            }));
        });

        // ── 批量軟刪除（POST body，避免 query string 長度 / encoding 問題）────
        nya.MapPost("/users/{userId}/messages/soft-delete", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var body = RequestBodyHelper.GetBody(ctx);

            if (!body.TryGetProperty("message_ids", out var idsEl) ||
                idsEl.ValueKind != JsonValueKind.Array)
                return Results.BadRequest(ApiResponseHelper.Error("Request body must contain { \"message_ids\": [\"id1\", \"id2\", …] }"));

            var ids = idsEl.EnumerateArray()
                .Where(el => el.ValueKind == JsonValueKind.String)
                .Select(el => el.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .Take(50)
                .ToList();

            if (ids.Count == 0)
                return Results.BadRequest(ApiResponseHelper.Error("message_ids 不可為空。"));

            var count = memoryService.DeleteMessagesByIds(userId, ids);
            auditLogger.LogMemoryDelete(userId, "messages_selective", $"{count} of {ids.Count} ids");

            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id      = userId,
                soft_deleted = count,
                selective    = true,
                requested_ids = ids.Count
            }));
        });

        // ── 話題管理 ────────────────────────────────────────────────────────
        nya.MapGet("/users/{userId}/topics", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var topics = memoryService.ListTopicsWithCount(userId);
            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                total   = topics.Count,
                topics  = topics.Select(t => new
                {
                    topic_id   = t.TopicId,
                    title      = t.Title,
                    is_active  = t.IsActive,
                    msg_count  = t.MsgCount,
                    created_at = t.CreatedAt
                })
            }));
        });

        nya.MapPost("/users/{userId}/topics", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var body = RequestBodyHelper.GetBody(ctx);
            string? title = null;
            if (body.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                title = titleEl.GetString();

            var topic = memoryService.CreateNewTopic(userId, title);
            auditLogger.LogTopicCreate(userId, topic.TopicId, topic.Title);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id   = userId,
                topic_id  = topic.TopicId,
                title     = topic.Title,
                is_active = topic.IsActive,
                created_at = topic.CreatedAt
            }));
        });

        nya.MapPost("/users/{userId}/topics/{topicId}/switch", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId,
            string topicId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var currentActive = memoryService.ListTopicsWithCount(userId).FirstOrDefault(t => t.IsActive);
            var switched = memoryService.SwitchTopic(userId, topicId);

            if (switched == null)
                return Results.NotFound(ApiResponseHelper.Error($"Topic '{topicId}' not found for user.", 404));

            auditLogger.LogTopicSwitch(userId, currentActive?.TopicId, switched.TopicId);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id    = userId,
                topic_id   = switched.TopicId,
                title      = switched.Title,
                is_active  = switched.IsActive,
                created_at = switched.CreatedAt
            }));
        });

        // 大項#4：重新命名話題（供網頁 / 客戶端使用；LLM 走 rename_topic 工具）。
        nya.MapPost("/users/{userId}/topics/{topicId}/rename", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId,
            string topicId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var body = RequestBodyHelper.GetBody(ctx);
            if (!RequestBodyHelper.TryGetRequired(body, "title", out var title, out var error))
                return error!;

            var renamed = memoryService.RenameTopic(userId, topicId, title);
            if (renamed == null)
                return Results.NotFound(ApiResponseHelper.Error($"Topic '{topicId}' not found for user.", 404));

            auditLogger.LogTopicRename(userId, renamed.TopicId, title);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id    = userId,
                topic_id   = renamed.TopicId,
                title      = renamed.Title,
                is_active  = renamed.IsActive,
                created_at = renamed.CreatedAt
            }));
        });

        // 大項#2：刪除話題（管理端專用、不開放 LLM）。連同其訊息（軟刪除）與摘要一併清除。
        nya.MapDelete("/users/{userId}/topics/{topicId}", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId,
            string topicId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var deleted = memoryService.DeleteTopic(userId, topicId);
            if (!deleted)
                return Results.NotFound(ApiResponseHelper.Error($"Topic '{topicId}' not found for user.", 404));

            auditLogger.LogMemoryDelete(userId, "topic", topicId);
            return Results.Ok(ApiResponseHelper.Success(new { user_id = userId, topic_id = topicId, deleted = true }));
        });

        // ── 事實管理 ────────────────────────────────────────────────────────
        nya.MapGet("/users/{userId}/facts", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var facts = memoryService.GetCurrentFacts(userId);
            var now = DateTime.UtcNow;
            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                total = facts.Count,
                facts = facts.Select(f => new
                {
                    fact_id           = f.FactId,
                    category          = f.Category,
                    fact_key          = f.FactKey,
                    fact_value        = f.FactValue,
                    confidence        = f.Confidence,
                    version           = f.Version,
                    source_message_id = f.SourceMessageId,
                    source_text       = f.SourceText,
                    expires_at        = f.ExpiresAt == null ? null : DateTime.SpecifyKind(f.ExpiresAt.Value, DateTimeKind.Utc).ToString("O"),
                    is_expired        = f.ExpiresAt != null && f.ExpiresAt <= now,
                    created_at        = f.CreatedAt
                })
            }));
        });

        nya.MapGet("/users/{userId}/facts/{factKey}/history", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            string userId,
            string factKey) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var history = memoryService.GetFactHistory(userId, factKey);
            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                fact_key = factKey,
                total = history.Count,
                history = history.Select(f => new
                {
                    fact_id       = f.FactId,
                    category      = f.Category,
                    fact_value    = f.FactValue,
                    source_text   = f.SourceText,
                    expires_at    = f.ExpiresAt == null ? null : DateTime.SpecifyKind(f.ExpiresAt.Value, DateTimeKind.Utc).ToString("O"),
                    confidence    = f.Confidence,
                    version       = f.Version,
                    is_active     = f.IsActive,
                    created_at    = f.CreatedAt,
                    superseded_at = f.SupersededAt
                })
            }));
        });

        nya.MapGet("/users/{userId}/forgotten", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var forgotten = memoryService.GetForgotten(userId);
            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                total = forgotten.Count,
                forgotten = forgotten.Select(f => new
                {
                    fact_key = f.FactKey,
                    fact_value = f.FactValue,
                    match_terms = NyaMemoryService.GetForgottenMatchTerms(f),
                    through_sequence = f.ThroughSequence,
                    created_at = f.CreatedAt
                })
            }));
        });

        nya.MapDelete("/users/{userId}/forgotten/{factKey}", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId,
            string factKey) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var deleted = memoryService.ClearForgotten(userId, factKey);
            if (deleted == 0)
                return Results.NotFound(ApiResponseHelper.Error("Forgotten record not found.", 404));

            auditLogger.LogMemoryDelete(userId, "forgotten", factKey);
            return Results.Ok(ApiResponseHelper.Success(new { user_id = userId, fact_key = factKey, deleted }));
        });

        nya.MapPost("/users/{userId}/facts/{factKey}/forget", (NyaMemoryService memoryService,
            NyaAuditLogger auditLogger, string userId, string factKey) =>
        {
            if (!memoryService.ForgetFact(userId, factKey, coversPendingTurn: false))
                return Results.NotFound(ApiResponseHelper.Error("Note not found.", 404));
            auditLogger.LogMemoryDelete(userId, "note_forget", factKey);
            return Results.Ok(ApiResponseHelper.Success(new { user_id = userId, fact_key = factKey, forgotten = true }));
        });

        nya.MapPost("/users/{userId}/facts/{factKey}/rollback", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId,
            string factKey) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var body = RequestBodyHelper.GetBody(ctx);
            int? targetVersion = null;
            if (body.TryGetProperty("target_version", out var tv) && tv.ValueKind == System.Text.Json.JsonValueKind.Number)
                targetVersion = tv.GetInt32();

            var current = memoryService.GetActiveFacts(userId).FirstOrDefault(f => f.FactKey == factKey);
            var rolled = memoryService.RollbackFact(userId, factKey, targetVersion);

            if (rolled == null)
                return Results.BadRequest(ApiResponseHelper.Error("No previous version to rollback to."));

            auditLogger.LogFactRollback(userId, factKey, current?.Version ?? 0, rolled.Version);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                fact_key = factKey,
                rolled_to_version = rolled.Version,
                fact_value = rolled.FactValue
            }));
        });

        nya.MapDelete("/users/{userId}/facts", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var count = memoryService.DeleteAllFacts(userId);
            auditLogger.LogMemoryDelete(userId, "facts");
            return Results.Ok(ApiResponseHelper.Success(new { user_id = userId, deleted = count }));
        });

        // ── NEW-22: 硬刪除單一事實 ───────────────────────────────────────────
        nya.MapDelete("/users/{userId}/facts/{factKey}", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId,
            string factKey) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var count = memoryService.DeleteFact(userId, factKey);
            if (count == 0)
                return Results.NotFound(ApiResponseHelper.Error($"Fact '{factKey}' not found for user.", 404));

            auditLogger.LogMemoryDelete(userId, "fact_key", factKey);
            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                fact_key = factKey,
                deleted_versions = count
            }));
        });

        // ── 摘要管理 ────────────────────────────────────────────────────────
        nya.MapGet("/users/{userId}/summaries", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            INyaConfigStore<NyaChatConfig> configStore,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var history = memoryService.GetSummaryHistory(userId);
            // 大項#3：摘要依話題獨立 → 附上所屬話題，前端才能辨識是哪個話題的摘要。
            var topics = memoryService.ListTopicsWithCount(userId);
            var topicTitles = topics.ToDictionary(t => t.TopicId, t => t.Title);
            // Fix 2（觸發透明度）：每個話題的未摘要(active)訊息數 + 自動摘要門檻，
            // 前端可顯示「未摘要 N 則 · 距下次自動摘要還差 max(0, trigger−N) 則」。
            var triggerCount = configStore.Current.SummarizeTriggerCount;
            var topicActive = topics.ToDictionary(
                t => t.TopicId,
                t => memoryService.CountActiveMessages(userId, t.TopicId));
            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                total = history.Count,
                trigger_count = triggerCount,
                auto_summarize_enabled = configStore.Current.AutoSummarizeEnabled,
                topic_active = topicActive,
                summaries = history.Select(s => new
                {
                    summary_id       = s.SummaryId,
                    version          = s.Version,
                    is_active        = s.IsActive,
                    topic_id         = s.TopicId,
                    topic_title      = s.TopicId != null && topicTitles.TryGetValue(s.TopicId, out var tt) ? tt : null,
                    summary_text     = s.SummaryText,
                    summary_preview  = s.SummaryText.Length > 150 ? s.SummaryText[..150] + "…" : s.SummaryText,
                    covered_from_seq = s.CoveredFromSeq,
                    covered_to_seq   = s.CoveredToSeq,
                    message_count    = s.MessageCount,
                    created_at       = s.CreatedAt,
                    superseded_at    = s.SupersededAt
                })
            }));
        });

        nya.MapPost("/users/{userId}/summaries/trigger", async (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaSummarizer summarizer,
            NyaMemoryService memoryService,
            INyaConfigStore<NyaChatConfig> configStore,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            // 大項#3：摘要依話題獨立——對使用者當前 active 話題觸發摘要。
            // 手動「立即摘要」用 force=true 略過自動觸發門檻（否則切換/新開話題後 active 話題訊息常不足而無作用）。
            if (!configStore.Current.AutoSummarizeEnabled)
                return Results.Ok(ApiResponseHelper.Success(new
                {
                    user_id = userId,
                    triggered = false,
                    status = "Disabled",
                    message = "自動摘要已關閉；現有摘要不會用於聊天。"
                }));

            var topicId = memoryService.GetOrCreateActiveTopic(userId);
            var outcome = await summarizer.TrySummarizeAsync(userId, topicId, force: true);

            // 用 outcome 顯示精確原因——尤其區分「訊息不足」與「LLM 回空」（Root cause A 過去被誤報為訊息不足）。
            var triggered = outcome.Created;
            NyaSummary? after = triggered ? memoryService.GetActiveSummaries(userId, topicId).FirstOrDefault() : null;
            var message = outcome.Status switch
            {
                NyaSummarizeStatus.Created           => "摘要已生成",
                NyaSummarizeStatus.NotEnoughMessages => "目前話題沒有足夠可壓縮的舊訊息（保留最近訊息後沒有更早的可摘要內容）。",
                NyaSummarizeStatus.LlmEmpty          => "LLM 回傳空內容，未產生摘要（可能 thinking 模型耗盡輸出預算：請提高 SummarizeMaxOutputTokens 或關閉 thinking）。",
                _                                    => "摘要產生時發生錯誤，請查看稽核日誌。"
            };
            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id         = userId,
                triggered,
                topic_id        = topicId,
                status          = outcome.Status.ToString(),
                new_version     = triggered ? after?.Version : (int?)null,
                covered_to_seq  = triggered ? after?.CoveredToSeq : (long?)null,
                message
            }));
        });

        nya.MapPost("/users/{userId}/summaries/{summaryId}/rollback", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId,
            string summaryId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var current = memoryService.GetActiveSummaries(userId).FirstOrDefault();
            var rolled = memoryService.RollbackSummary(userId, summaryId);

            if (rolled == null)
                return Results.NotFound(ApiResponseHelper.Error("Summary not found or cannot be rolled back.", 404));

            auditLogger.LogSummaryRollback(userId, summaryId, current?.Version ?? 0, rolled.Version);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                summary_id = summaryId,
                rolled_to_version = rolled.Version,
                covered_from_seq = rolled.CoveredFromSeq,
                covered_to_seq = rolled.CoveredToSeq
            }));
        });

        nya.MapDelete("/users/{userId}/summaries", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var count = memoryService.DeleteAllSummaries(userId);
            auditLogger.LogMemoryDelete(userId, "summaries");
            return Results.Ok(ApiResponseHelper.Success(new { user_id = userId, deleted = count }));
        });

        // ── 審計日誌 ────────────────────────────────────────────────────────
        nya.MapGet("/audit/traces/{traceId}", (NyaAuditLogger auditLogger, string traceId) =>
            Results.Ok(ApiResponseHelper.Success(new
            {
                trace_id = traceId,
                logs = auditLogger.GetByTraceId(traceId).Select(lg => new
                {
                    user_id = lg.UserId, log_id = lg.LogId, trace_id = lg.TraceId,
                    action = lg.Action, result = lg.Result, detail_json = lg.DetailJson, created_at = lg.CreatedAt
                })
            })));

        nya.MapGet("/users/{userId}/audit", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaAuditLogger auditLogger,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var actionFilter = ctx.Request.Query["action"].ToString();
            var resultFilter = ctx.Request.Query["result"].ToString();
            var keyword = ctx.Request.Query["keyword"].ToString();
            var limit  = int.TryParse(ctx.Request.Query["limit"].ToString(),  out var l) ? l : 50;
            var offset = int.TryParse(ctx.Request.Query["offset"].ToString(), out var o) ? o : 0;

            var (logs, total) = auditLogger.GetLogs(
                userId,
                string.IsNullOrWhiteSpace(actionFilter) ? null : actionFilter,
                limit: limit,
                offset: offset,
                keyword: string.IsNullOrWhiteSpace(keyword) ? null : keyword,
                result: string.IsNullOrWhiteSpace(resultFilter) ? null : resultFilter);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                total,
                write_failure_count = auditLogger.WriteFailureCount,
                limit,
                offset,
                keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword,
                logs = logs.Select(lg => new
                {
                    log_id      = lg.LogId,
                    trace_id    = lg.TraceId,
                    action      = lg.Action,
                    result      = lg.Result,
                    detail_json = lg.DetailJson,
                    created_at  = lg.CreatedAt
                })
            }));
        });

        nya.MapGet("/audit", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaAuditLogger auditLogger) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var userIdFilter = ctx.Request.Query["user_id"].ToString();
            var actionFilter = ctx.Request.Query["action"].ToString();
            var resultFilter = ctx.Request.Query["result"].ToString();
            var keyword = ctx.Request.Query["keyword"].ToString();
            var limit  = int.TryParse(ctx.Request.Query["limit"].ToString(),  out var l) ? l : 50;
            var offset = int.TryParse(ctx.Request.Query["offset"].ToString(), out var o) ? o : 0;

            var (logs, total) = auditLogger.GetLogs(
                string.IsNullOrWhiteSpace(userIdFilter) ? null : userIdFilter,
                string.IsNullOrWhiteSpace(actionFilter) ? null : actionFilter,
                limit: limit,
                offset: offset,
                keyword: string.IsNullOrWhiteSpace(keyword) ? null : keyword,
                result: string.IsNullOrWhiteSpace(resultFilter) ? null : resultFilter);

            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = string.IsNullOrWhiteSpace(userIdFilter) ? null : userIdFilter,
                total,
                limit,
                offset,
                // 稽核管線健康度：>0 代表稽核 DB 寫入正在掉資料（Phase 4-C 兜底計數器，先前無人讀取）。
                write_failure_count = auditLogger.WriteFailureCount,
                keyword = string.IsNullOrWhiteSpace(keyword) ? null : keyword,
                logs = logs.Select(lg => new
                {
                    user_id     = lg.UserId,
                    log_id      = lg.LogId,
                    trace_id    = lg.TraceId,
                    action      = lg.Action,
                    result      = lg.Result,
                    detail_json = lg.DetailJson,
                    created_at  = lg.CreatedAt
                })
            }));
        });

        // ── 稽核日誌刪除（大項#3，管理端專用、不開放 LLM）────────────────────
        // 刪除單筆稽核日誌。
        nya.MapDelete("/audit/{logId}", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaAuditLogger auditLogger,
            string logId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var deleted = auditLogger.DeleteLog(logId);
            if (deleted == 0)
                return Results.NotFound(ApiResponseHelper.Error($"Audit log '{logId}' not found.", 404));
            auditLogger.LogAuditPurge("single", deleted, logId);
            return Results.Ok(ApiResponseHelper.Success(new { log_id = logId, deleted }));
        });

        // 清空某使用者的稽核日誌（需 ?confirm=all）；可帶 action / keyword / result，只刪符合篩選的部分。
        nya.MapDelete("/users/{userId}/audit", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaAuditLogger auditLogger,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            if (!string.Equals(ctx.Request.Query["confirm"].ToString(), "all", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(ApiResponseHelper.Error("Clearing a user's audit log requires ?confirm=all."));

            string? action = QueryOrNull(ctx, "action"), keyword = QueryOrNull(ctx, "keyword"), result = QueryOrNull(ctx, "result");
            var deleted = auditLogger.DeleteLogs(userId, action, keyword, result);
            var filtered = action != null || keyword != null || result != null;
            auditLogger.LogAuditPurge("user", deleted, userId, filtered ? new { action, keyword, result } : null);
            return Results.Ok(ApiResponseHelper.Success(new { user_id = userId, deleted }));
        });

        // 清空稽核日誌（需 ?confirm=all）；帶 user_id / action / keyword / result 時只刪符合篩選的部分，都不帶 = 全部。
        nya.MapDelete("/audit", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaAuditLogger auditLogger) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            if (!string.Equals(ctx.Request.Query["confirm"].ToString(), "all", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(ApiResponseHelper.Error("Clearing audit logs requires ?confirm=all."));

            string? userId = QueryOrNull(ctx, "user_id"), action = QueryOrNull(ctx, "action"),
                    keyword = QueryOrNull(ctx, "keyword"), result = QueryOrNull(ctx, "result");
            var deleted = auditLogger.DeleteLogs(userId, action, keyword, result);
            var filtered = userId != null || action != null || keyword != null || result != null;
            auditLogger.LogAuditPurge(filtered ? "filter" : "all", deleted, null,
                filtered ? new { user_id = userId, action, keyword, result } : null);
            return Results.Ok(ApiResponseHelper.Success(new { deleted }));
        });

        // ── 清除所有記憶 ─────────────────────────────────────────────────────
        nya.MapDelete("/users/{userId}", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            NyaMemoryService memoryService,
            NyaAuditLogger auditLogger,
            string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var confirmAll = ctx.Request.Query["confirm"].ToString();
            if (!string.Equals(confirmAll, "all", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(ApiResponseHelper.Error("Full memory clear requires ?confirm=all query parameter."));

            var msgs = memoryService.DeleteAllMessages(userId);
            var facts = memoryService.DeleteAllFacts(userId);
            var summaries = memoryService.DeleteAllSummaries(userId);
            var topics = memoryService.DeleteAllTopics(userId);
            var forgotten = memoryService.ClearForgotten(userId);
            auditLogger.LogMemoryDelete(userId, "all");

            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                soft_deleted_messages = msgs,
                deleted_facts = facts,
                deleted_summaries = summaries,
                deleted_topics = topics,
                cleared_forgotten = forgotten
            }));
        });

        // ── Doc 3 Plan C：LLM profile / route / catalog 管理 ─────────────────

        // GET /llm/param-catalog：靜態參數目錄（供 UI 渲染表單欄位）。
        nya.MapGet("/llm/param-catalog", (
            HttpContext ctx,
            LocalAdminAuthService auth) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            return Results.Ok(ApiResponseHelper.Success(NyaLlmParamCatalog.All));
        });

        // GET /llm/templates：供應商家族模板（供 UI 預填 provider/base_url/params）。
        nya.MapGet("/llm/templates", (
            HttpContext ctx,
            LocalAdminAuthService auth) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            return Results.Ok(ApiResponseHelper.Success(NyaLlmProviderTemplates.All));
        });

        nya.MapGet("/llm/call-sites", (HttpContext ctx, LocalAdminAuthService auth) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            return Results.Ok(ApiResponseHelper.Success(new { call_sites = NyaLlmAdminSettings.CallSites }));
        });

        nya.MapGet("/llm/profiles", (HttpContext ctx, LocalAdminAuthService auth, NyaLlmProfileStore store) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var routes = store.ListRoutes();
            var profiles = store.ListProfiles().Select(e => new
            {
                profile_id = e.ProfileId, provider = e.Provider, base_url = e.BaseUrl,
                api_key = NyaSecretMask.Mask(e.ApiKey), model = e.Model, template = e.Template, params_json = e.ParamsJson,
                call_sites = NyaLlmAdminSettings.CallSites.Where(cs =>
                    (routes.FirstOrDefault(r => r.CallSiteId == cs) ?? routes.FirstOrDefault(r => r.CallSiteId == NyaLlmProfileStore.DefaultCallSite))?.ProfileId == e.ProfileId).ToArray(),
                effective = NyaLlmAdminSettings.Effective(e.ToProfile())
            });
            return Results.Ok(ApiResponseHelper.Success(profiles));
        });

        static IResult SaveProfile(HttpContext ctx, LocalAdminAuthService auth, NyaLlmProfileStore store,
            IEnumerable<INyaLlmProvider> providers, NyaAuditLogger auditLogger, string? id)
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var body = RequestBodyHelper.GetBody(ctx);
            if (body.ValueKind != JsonValueKind.Object) return Results.BadRequest(ApiResponseHelper.Error("Profile 必須是 JSON object。"));
            var creating = id == null;
            if (creating)
            {
                if (!RequestBodyHelper.TryGetRequired(body, "profile_id", out var profileId, out var error)) return error!;
                id = profileId;
            }
            var existing = store.GetProfile(id!);
            if (creating && existing != null) return Results.Conflict(ApiResponseHelper.Error("Profile ID 已存在。", 409));
            if (!creating && existing == null) return Results.NotFound(ApiResponseHelper.Error("Profile 不存在。", 404));
            foreach (var field in new[] { "provider", "base_url", "api_key", "model", "template", "params_json" })
                if (body.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.String)
                    return Results.BadRequest(ApiResponseHelper.Error(field + " 必須是字串。"));
            string Read(string key, string fallback = "") => body.TryGetProperty(key, out var value) ? value.GetString() ?? fallback : fallback;
            var entry = new NyaLlmProfileEntry
            {
                ProfileId = id!, Provider = Read("provider").Trim().ToLowerInvariant(), BaseUrl = Read("base_url").Trim(),
                ApiKey = Read("api_key"), Model = Read("model").Trim(), Template = Read("template"), ParamsJson = Read("params_json", "{}")
            };
            var validation = NyaLlmAdminSettings.Validate(entry, providers);
            if (validation != null) return Results.BadRequest(ApiResponseHelper.Error(validation));
            if (creating)
            {
                if (entry.ApiKey.Contains('*')) return Results.BadRequest(ApiResponseHelper.Error("新增 Profile 不可使用遮罩金鑰。"));
                if (!store.TryCreateProfile(entry)) return Results.Conflict(ApiResponseHelper.Error("Profile ID 已存在。", 409));
            }
            else if (!store.TryUpdateProfile(entry)) return Results.NotFound(ApiResponseHelper.Error("Profile 不存在。", 404));
            auditLogger.LogLlmProfileUpsert(entry.ProfileId, entry.Provider, entry.Model);
            return Results.Ok(ApiResponseHelper.Success(new { profile_id = entry.ProfileId }));
        }

        nya.MapPost("/llm/profiles", (HttpContext ctx, LocalAdminAuthService auth, NyaLlmProfileStore store,
            IEnumerable<INyaLlmProvider> providers, NyaAuditLogger auditLogger) => SaveProfile(ctx, auth, store, providers, auditLogger, null));
        nya.MapPut("/llm/profiles/{id}", (HttpContext ctx, LocalAdminAuthService auth, NyaLlmProfileStore store,
            IEnumerable<INyaLlmProvider> providers, NyaAuditLogger auditLogger, string id) => SaveProfile(ctx, auth, store, providers, auditLogger, id));

        nya.MapGet("/llm/profiles/{id}/effective", (HttpContext ctx, LocalAdminAuthService auth, NyaLlmProfileStore store, string id) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var entry = store.GetProfile(id);
            return entry == null ? Results.NotFound(ApiResponseHelper.Error("Profile 不存在。", 404))
                : Results.Ok(ApiResponseHelper.Success(NyaLlmAdminSettings.Effective(entry.ToProfile())));
        });

        nya.MapPost("/llm/profiles/{id}/test", async (HttpContext ctx, LocalAdminAuthService auth, NyaLlmProfileStore store,
            IEnumerable<INyaLlmProvider> providers, string id) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var entry = store.GetProfile(id);
            if (entry == null) return Results.NotFound(ApiResponseHelper.Error("Profile 不存在。", 404));
            var validation = NyaLlmAdminSettings.Validate(entry, providers);
            if (validation != null) return Results.BadRequest(ApiResponseHelper.Error(validation));
            var profile = entry.ToProfile();
            var provider = providers.First(p => p.Key.Equals(profile.Provider, StringComparison.OrdinalIgnoreCase));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string? error = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(30, profile.TimeoutSeconds)));
                var response = await provider.SendAsync(new NyaLlmProviderRequest
                {
                    Profile = profile,
                    Request = new NyaLlmRequest
                    {
                        Model = profile.Model, Temperature = profile.Temperature, TopP = profile.TopP, MaxTokens = 32,
                        Messages = new() { new NyaLlmMessage { Role = "user", Content = "Reply OK." } }
                    }
                }, timeout.Token);
                if (response == null) error = "Provider 未返回有效回應，請檢查完整 API 端點、模型與金鑰。";
                else if (string.IsNullOrWhiteSpace(response.Content)) error = "模型回覆為空；思考模型可能需要調整思考設定。";
            }
            catch (OperationCanceledException) { error = "連線測試逾時或已取消（最長 30 秒）。"; }
            catch (HttpRequestException ex) { error = "HTTP 連線失敗" + (ex.StatusCode.HasValue ? "：" + (int)ex.StatusCode.Value : "，請檢查模型伺服器。"); }
            catch (Exception) { error = "Provider 測試失敗，請檢查模型伺服器回應與設定。"; }
            return Results.Ok(ApiResponseHelper.Success(new { ok = error == null, latency_ms = watch.ElapsedMilliseconds, error }));
        });

        nya.MapDelete("/llm/profiles/{id}", (HttpContext ctx, LocalAdminAuthService auth,
            NyaLlmProfileStore store, NyaAuditLogger auditLogger, string id) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            bool ok;
            try { ok = store.DeleteProfile(id); }
            catch (InvalidOperationException ex) { return Results.Conflict(ApiResponseHelper.Error(ex.Message, 409)); }
            if (ok) auditLogger.LogLlmProfileDelete(id);
            return ok ? Results.Ok(ApiResponseHelper.Success(new { deleted = id })) : Results.NotFound(ApiResponseHelper.Error("not found", 404));
        });

        nya.MapGet("/llm/routes", (HttpContext ctx, LocalAdminAuthService auth, NyaLlmProfileStore store, INyaConfigStore<NyaChatConfig> configStore) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var routes = store.ListRoutes();
            var effective = NyaLlmAdminSettings.CallSites.Select(cs =>
            {
                var direct = routes.FirstOrDefault(r => r.CallSiteId == cs);
                var resolved = direct ?? routes.FirstOrDefault(r => r.CallSiteId == NyaLlmProfileStore.DefaultCallSite);
                var profile = resolved == null ? null : store.GetProfile(resolved.ProfileId);
                return new
                {
                    call_site_id = cs, profile_id = direct?.ProfileId,
                    execution_enabled = cs == NyaLlmTasks.Chat || (cs == NyaLlmTasks.Summarization && configStore.Current.AutoSummarizeEnabled),
                    effective_profile_id = profile?.ProfileId, provider = profile?.Provider, model = profile?.Model,
                    source = direct == null ? NyaLlmProfileStore.DefaultCallSite : cs
                };
            });
            return Results.Ok(ApiResponseHelper.Success(effective));
        });

        nya.MapPut("/llm/routes", (HttpContext ctx, LocalAdminAuthService auth, NyaLlmProfileStore store, NyaAuditLogger auditLogger) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var body = RequestBodyHelper.GetBody(ctx);
            if (body.ValueKind != JsonValueKind.Object) return Results.BadRequest(ApiResponseHelper.Error("路由必須是 JSON object。"));
            if (!RequestBodyHelper.TryGetRequired(body, "call_site_id", out var callSiteId, out var e1)) return e1!;
            if (!RequestBodyHelper.TryGetRequired(body, "profile_id", out var profileId, out var e2)) return e2!;
            if (!NyaLlmAdminSettings.CallSites.Contains(callSiteId)) return Results.BadRequest(ApiResponseHelper.Error("未知 LLM 呼叫點：" + callSiteId));
            try
            {
                if (profileId == NyaLlmProfileStore.DefaultCallSite) store.ClearRoute(callSiteId);
                else store.SetRoute(callSiteId, profileId);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(ApiResponseHelper.Error(ex.Message, 400)); }
            auditLogger.LogLlmRouteSet(callSiteId, profileId);
            return Results.Ok(ApiResponseHelper.Success(new { call_site_id = callSiteId, profile_id = profileId }));
        });

    }

    internal static bool TryResolveRouteAuthorization(
        string method,
        string path,
        out string[] permissions,
        out bool superAdminOnly)
    {
        permissions = Array.Empty<string>();
        superAdminOnly = false;
        if (!path.StartsWith("/", StringComparison.Ordinal))
            return false;

        var segments = path[1..].TrimEnd('/').ToLowerInvariant().Split('/');
        if (segments.Any(string.IsNullOrEmpty))
            return false;

        var verb = method.ToUpperInvariant();
        var audit = segments is ["audit"] or ["audit", _] or ["users", _, "audit"];
        var profiles = segments is ["llm", "profiles"] or ["llm", "profiles", _];
        if ((verb == HttpMethods.Delete && audit) ||
            ((verb is "POST" or "PUT" or "DELETE") && profiles) ||
            (verb == HttpMethods.Put && segments is ["llm", "routes"]) ||
            (verb == HttpMethods.Post && segments is ["llm", "profiles", _, "test"]))
        {
            superAdminOnly = true;
            return true;
        }

        if (verb == HttpMethods.Get)
        {
            if (segments is ["audit"] or ["audit", "traces", _] or ["users", _, "audit"])
                permissions = new[] { LocalAdminPermissions.AuditRead };
            else if (segments is ["users"] or
                     ["users", _, "overview" or "messages" or "topics" or "facts" or "summaries" or "forgotten"] or
                     ["users", _, "messages", _] or
                     ["users", _, "facts", _, "history"])
                permissions = new[] { LocalAdminPermissions.PermissionUserManage, LocalAdminPermissions.AuditRead };
            else if (segments is ["tools", "availability"])
                permissions = new[] { LocalAdminPermissions.SystemStatusRead, LocalAdminPermissions.AuditRead, LocalAdminPermissions.PermissionUserManage };
            else if (segments is ["status" or "config" or "prompts" or "tools" or "souls"] or ["souls", _] or ["config", "effective"])
                permissions = new[] { LocalAdminPermissions.SystemStatusRead, LocalAdminPermissions.AuditRead };
            else if (segments is ["llm", "param-catalog" or "templates" or "call-sites" or "profiles" or "routes"] or ["llm", "profiles", _, "effective"])
                permissions = new[] { LocalAdminPermissions.SystemStatusRead };
        }
        else if ((verb == HttpMethods.Put && segments is ["config"] or ["souls", _]) ||
                 (verb == HttpMethods.Patch && segments is ["config"]) ||
                 (verb == HttpMethods.Post && segments is ["config", "reset"]) ||
                 (verb == HttpMethods.Post && segments is ["prompts", "reload"] or ["tools", _, "toggle"] or ["souls"] or ["souls", "reload"]) ||
                 (verb == HttpMethods.Delete && segments is ["souls", _]))
        {
            permissions = new[] { LocalAdminPermissions.SystemWorkerManage };
        }
        else if ((verb == HttpMethods.Delete && segments is ["users", _] or ["users", _, "soul"] or ["users", _, "forgotten", _]) ||
                 ((verb is "POST" or "PUT" or "DELETE") && segments is
                     ["users", _, "facts" or "messages" or "topics" or "summaries" or "soul"] or
                     ["users", _, "facts" or "messages" or "topics", _] or
                     ["users", _, "facts", _, "rollback" or "forget"] or
                     ["users", _, "topics", _, "switch" or "rename"] or
                     ["users", _, "summaries", "trigger"] or
                     ["users", _, "summaries", _, "rollback"]))
        {
            permissions = new[] { LocalAdminPermissions.PermissionUserManage };
        }

        return permissions.Length > 0;
    }

    // 角色管理已交還 broker 操作員與 LINE 權限後台（2026-10-14）；保留程式但不載入。
    private static void MapRoleAdmin(RouteGroupBuilder nya)
    {
        static string[] ReadStrArr(System.Text.Json.JsonElement body, string prop)
        {
            if (!body.TryGetProperty(prop, out var el) || el.ValueKind != System.Text.Json.JsonValueKind.Array)
                return Array.Empty<string>();
            return el.EnumerateArray()
                .Where(e => e.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(e => e.GetString()!).ToArray();
        }

        // ── 身分組（roles / capabilities / user-roles，items 1–2）──────────────────
        nya.MapGet("/roles", (HttpContext ctx, LocalAdminAuthService auth, Broker.Services.NyaTools.NyaRoleAdminService roles) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            // 明確投影為 snake_case：local-admin 走明文路徑，預設 camelCase 序列化，
            // 直接回傳 record 會變 camelCase，前端讀 snake_case 會拿到 undefined。
            var rows = roles.ListRoles().Select(r => new
            {
                role_id              = r.RoleId,
                display_name         = r.DisplayName,
                allowed_task_types   = r.AllowedTaskTypes,
                capability_count     = r.CapabilityCount,
                all_capabilities     = r.AllCapabilities,
                active_binding_count = r.ActiveBindingCount,
                built_in             = r.BuiltIn
            });
            return Results.Ok(ApiResponseHelper.Success(new { roles = rows }));
        });

        nya.MapGet("/roles/{id}", (HttpContext ctx, LocalAdminAuthService auth, Broker.Services.NyaTools.NyaRoleAdminService roles, string id) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var d = roles.GetRole(id);
            if (d == null) return Results.NotFound(ApiResponseHelper.Error("角色不存在", 404));
            return Results.Ok(ApiResponseHelper.Success(new
            {
                role_id            = d.RoleId,
                display_name       = d.DisplayName,
                allowed_task_types = d.AllowedTaskTypes,
                capability_ids     = d.CapabilityIds,
                all_capabilities   = d.AllCapabilities,
                version            = d.Version,
                built_in           = d.BuiltIn
            }));
        });

        nya.MapPost("/roles", (HttpContext ctx, LocalAdminAuthService auth, Broker.Services.NyaTools.NyaRoleAdminService roles) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var b = RequestBodyHelper.GetBody(ctx);
            var roleId = b.TryGetProperty("role_id", out var idEl) ? idEl.GetString() ?? "" : "";
            var name = b.TryGetProperty("display_name", out var nEl) ? nEl.GetString() ?? "" : "";
            var (ok, err) = roles.CreateRole(new Broker.Services.NyaTools.NyaRoleInput(roleId, name, ReadStrArr(b, "allowed_task_types"), ReadStrArr(b, "capability_ids")));
            return ok ? Results.Ok(ApiResponseHelper.Success(new { role_id = roleId })) : Results.BadRequest(ApiResponseHelper.Error(err!, 400));
        });

        nya.MapPut("/roles/{id}", (HttpContext ctx, LocalAdminAuthService auth, Broker.Services.NyaTools.NyaRoleAdminService roles, string id) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var b = RequestBodyHelper.GetBody(ctx);
            var name = b.TryGetProperty("display_name", out var nEl) ? nEl.GetString() ?? "" : "";
            var (ok, err) = roles.UpdateRole(id, new Broker.Services.NyaTools.NyaRoleInput(id, name, ReadStrArr(b, "allowed_task_types"), ReadStrArr(b, "capability_ids")));
            return ok ? Results.Ok(ApiResponseHelper.Success(new { role_id = id })) : Results.BadRequest(ApiResponseHelper.Error(err!, 400));
        });

        nya.MapDelete("/roles/{id}", (HttpContext ctx, LocalAdminAuthService auth, Broker.Services.NyaTools.NyaRoleAdminService roles, string id) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var (ok, err) = roles.DeleteRole(id);
            return ok ? Results.Ok(ApiResponseHelper.Success(new { role_id = id })) : Results.Json(ApiResponseHelper.Error(err!, 409), statusCode: 409);
        });

        nya.MapGet("/capabilities", (HttpContext ctx, LocalAdminAuthService auth, Broker.Services.NyaTools.NyaRoleAdminService roles) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var caps = roles.ListCapabilities().Select(c => new
            {
                capability_id   = c.CapabilityId,
                route           = c.Route,
                resource_type   = c.ResourceType,
                risk_level      = c.RiskLevel,
                approval_policy = c.ApprovalPolicy
            });
            return Results.Ok(ApiResponseHelper.Success(new { capabilities = caps }));
        });

        nya.MapGet("/users/{userId}/roles", (HttpContext ctx, LocalAdminAuthService auth, Broker.Services.NyaTools.NyaRoleAdminService roles, string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var r = roles.GetUserRoles(userId);
            var rows = r.Roles.Select(x => new
            {
                role_id          = x.RoleId,
                display_name     = x.DisplayName,
                capability_count = x.CapabilityCount,
                granted_at       = x.GrantedAt,
                binding_id       = x.BindingId
            });
            return Results.Ok(ApiResponseHelper.Success(new { principal_id = r.PrincipalId, roles = rows }));
        });

        nya.MapPost("/users/{userId}/roles", (HttpContext ctx, LocalAdminAuthService auth, Broker.Services.NyaTools.NyaRoleAdminService roles, NyaAuditLogger auditLogger, string userId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            var b = RequestBodyHelper.GetBody(ctx);
            if (!b.TryGetProperty("role_id", out var rEl) || rEl.ValueKind != System.Text.Json.JsonValueKind.String)
                return Results.BadRequest(ApiResponseHelper.Error("需要 { role_id }", 400));
            var roleId = rEl.GetString()!;
            var (ok, err) = roles.AssignRole(userId, roleId, "nya-admin");
            if (!ok) return Results.BadRequest(ApiResponseHelper.Error(err!, 400));
            auditLogger.LogRoleChange(userId, "role_assign", roleId);
            return Results.Ok(ApiResponseHelper.Success(new { user_id = userId, role_id = roleId }));
        });

        nya.MapDelete("/users/{userId}/roles/{roleId}", (HttpContext ctx, LocalAdminAuthService auth, Broker.Services.NyaTools.NyaRoleAdminService roles, NyaAuditLogger auditLogger, string userId, string roleId) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            if (!roles.RevokeRole(userId, roleId)) return Results.NotFound(ApiResponseHelper.Error("找不到有效綁定", 404));
            auditLogger.LogRoleChange(userId, "role_revoke", roleId);
            return Results.Ok(ApiResponseHelper.Success(new { user_id = userId, role_id = roleId }));
        });
    }

    // ── 私有輔助 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 驗證大項三 token 預算欄位（ratio ∈ [0,1]、正整數上限、min ≤ max）。
    /// 回傳錯誤訊息字串；null = 通過。
    /// </summary>
    private static string? ValidateBudgetConfig(NyaChatConfig c)
    {
        if (c.OutputReserveRatio is < 0 or > 1) return "OutputReserveRatio must be within [0, 1].";
        if (c.FactTokenBudgetRatio is < 0 or > 1) return "FactTokenBudgetRatio must be within [0, 1].";
        if (c.SummaryTokenBudgetRatio is < 0 or > 1) return "SummaryTokenBudgetRatio must be within [0, 1].";
        if (c.FactTokenBudgetRatio + c.SummaryTokenBudgetRatio > 1)
            return "FactTokenBudgetRatio + SummaryTokenBudgetRatio must not exceed 1.";
        if (c.DefaultMaxContextTokens <= 0) return "DefaultMaxContextTokens must be positive.";
        if (c.MinOutputTokens <= 0) return "MinOutputTokens must be positive.";
        if (c.MaxOutputTokens <= 0) return "MaxOutputTokens must be positive.";
        if (c.MinOutputTokens > c.MaxOutputTokens) return "MinOutputTokens must be ≤ MaxOutputTokens.";
        if (c.ContextSafetyMargin < 0) return "ContextSafetyMargin must be ≥ 0.";
        if (c.PerToolResultMaxTokens <= 0) return "PerToolResultMaxTokens must be positive.";
        if (c.TokenEstimatorCjkPerChar <= 0) return "TokenEstimatorCjkPerChar must be positive.";
        if (c.TokenEstimatorLatinPerChar <= 0) return "TokenEstimatorLatinPerChar must be positive.";
        if (c.ShortTermMessageLimit <= 0) return "ShortTermMessageLimit must be positive.";
        if (c.SummarizeMaxOutputTokens <= 0) return "SummarizeMaxOutputTokens must be positive.";
        return null;
    }

    private static SoulDefinition? ParseSoulFromBody(JsonElement body, out IResult? error)
    {
        error = null;

        if (!body.TryGetProperty("soul_id", out var soulIdEl) ||
            soulIdEl.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(soulIdEl.GetString()))
        {
            error = Results.BadRequest(ApiResponseHelper.Error("Missing or empty required field: soul_id"));
            return null;
        }

        if (!body.TryGetProperty("display_name", out var displayEl) ||
            displayEl.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(displayEl.GetString()))
        {
            error = Results.BadRequest(ApiResponseHelper.Error("Missing or empty required field: display_name"));
            return null;
        }

        var soulId      = soulIdEl.GetString()!.Trim();
        var displayName = displayEl.GetString()!.Trim();

        // soul_id 只允許字母、數字、底線、連字號
        if (!System.Text.RegularExpressions.Regex.IsMatch(soulId, @"^[A-Za-z0-9_\-]{1,64}$"))
        {
            error = Results.BadRequest(ApiResponseHelper.Error("soul_id must be 1–64 chars: letters, digits, _ or -"));
            return null;
        }

        var personality = new SoulPersonality();
        if (body.TryGetProperty("personality", out var pEl) && pEl.ValueKind == JsonValueKind.Object)
        {
            if (pEl.TryGetProperty("tone",           out var v) && v.ValueKind == JsonValueKind.String) personality.Tone          = v.GetString() ?? "";
            if (pEl.TryGetProperty("language",       out     v) && v.ValueKind == JsonValueKind.String) personality.Language      = v.GetString() ?? "繁體中文";
            if (pEl.TryGetProperty("greeting_style", out     v) && v.ValueKind == JsonValueKind.String) personality.GreetingStyle = v.GetString() ?? "";
            if (pEl.TryGetProperty("humor_level",    out     v) && v.ValueKind == JsonValueKind.String) personality.HumorLevel    = v.GetString() ?? "";
            if (pEl.TryGetProperty("formality",      out     v) && v.ValueKind == JsonValueKind.String) personality.Formality     = v.GetString() ?? "";
        }

        var rules = new List<string>();
        if (body.TryGetProperty("rules", out var rulesEl) && rulesEl.ValueKind == JsonValueKind.Array)
            foreach (var item in rulesEl.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    rules.Add(item.GetString()!);

        var forbidden = new List<string>();
        if (body.TryGetProperty("forbidden", out var forbEl) && forbEl.ValueKind == JsonValueKind.Array)
            foreach (var item in forbEl.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    forbidden.Add(item.GetString()!);

        return new SoulDefinition
        {
            SoulId      = soulId,
            DisplayName = displayName,
            Personality = personality,
            Rules       = rules,
            Forbidden   = forbidden,
        };
    }
}
