using Broker.Helpers;
using Broker.Services;
using Broker.NyaChat;
using Broker.NyaChat.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.Endpoints;

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
            using var userLease = ctx.Request.Method != HttpMethods.Get && ctx.Request.RouteValues["userId"] is string userId
                ? await ctx.RequestServices.GetRequiredService<NyaMemoryService>().LockUserAsync(userId, ctx.RequestAborted) : null;
            try { return await next(filterContext); }
            catch (NyaMemoryException ex)
            {
                return Results.BadRequest(new { success = false, message = ex.Message, error = new { code = ex.Code, field = ex.Field } });
            }
        });

        MapMemoryTools(nya);

        static string? QueryOrNull(HttpContext ctx, string key)
        {
            var v = ctx.Request.Query[key].ToString();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

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
                    reason = unavailable.GetValueOrDefault(t.Schema.Name), parameters = SnakeCase(t.Schema.Parameters)
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
                return Results.NotFound(ApiResponseHelper.Error($"Unknown tool: {toolId}", 404));

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

        nya.MapGet("/status", (
            HttpContext ctx,
            LocalAdminAuthService auth,
            INyaConfigStore<NyaChatConfig> configStore,
            Broker.NyaChat.NyaLlmProfileStore store,
            NyaSoulProvider soulProvider) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var config = configStore.Current;
            var souls = soulProvider.ListSouls();

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

                task_routing = routing,
                profiles = store.ListProfiles().Select(e => new
                {
                    profile_id = e.ProfileId,
                    provider = e.Provider,
                    model = e.Model,
                    template = e.Template
                }),

                chat_model = routing[Broker.NyaChat.NyaLlmTasks.Chat].model,
                chat_provider = routing[Broker.NyaChat.NyaLlmTasks.Chat].provider,
                summarization_model = routing[Broker.NyaChat.NyaLlmTasks.Summarization].model,
                short_term_message_limit = config.ShortTermMessageLimit,
                summarize_trigger_count = config.SummarizeTriggerCount,
                summary_version_retention = config.SummaryVersionRetention,
                default_soul_id = config.DefaultSoulId,
                available_souls = souls.Select(s => new { soul_id = s.Id, display_name = s.DisplayName })
            }));
        });

        nya.MapGet("/config", (HttpContext ctx, LocalAdminAuthService auth, INyaConfigStore<NyaChatConfig> configStore) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied)) return denied;
            return Results.Ok(ApiResponseHelper.Success(PascalCase(NyaChatHotSettings.MaskSecrets(configStore.Current))));
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
                return Results.Ok(ApiResponseHelper.Success(new { changed, config = PascalCase(NyaChatHotSettings.MaskSecrets(configStore.Current)) }));
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
                        active_note_count = u.ActiveNoteCount,
                        last_message_at   = u.LastAt,
                        soul_id           = resolved.Soul.SoulId,
                        bound_soul_id     = resolved.BoundSoulId,
                        effective_soul_id = resolved.Soul.SoulId,
                        soul_reason       = resolved.Reason
                    };
                })
            }));
        });

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
            var recentHistory = CurrentHistoryIds(ctx, userId);
            var lastInput = auth.GetStatus(ctx).Permissions.Contains(LocalAdminPermissions.AuditRead)
                ? auditLogger.GetLastChatInput(userId) : null;
            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id  = ov.UserId,
                active_topic = activeTopic == null ? null : new { topic_id = activeTopic.TopicId, title = activeTopic.Title },
                available_souls = soulProvider.ListSouls().Select(s => new { soul_id = s.Id, display_name = s.DisplayName }),
                forgotten_count = memoryService.GetHideReferences(userId).Where(r => r.IsActive).Select(r => r.ForgetId).Distinct().Count(),
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
                notes = new
                {
                    total_active = ov.Notes.TotalActive,
                    categories   = ov.Notes.Categories
                },
                summaries = new
                {
                    total_versions        = ov.Summaries.TotalVersions,
                    active_count          = ov.Summaries.ActiveSummaryCount,
                    latest_covered_to_seq = ov.Summaries.LatestCoveredToSeq
                },
                last_message_at      = ov.LastMessageAt,
                last_note_updated_at = ov.LastNoteUpdatedAt
            }));
        });

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
                origin_topic_id = msg.OriginTopicId, turn_id = msg.TurnId, turn_status = memoryService.GetTurn(userId, msg.TurnId)?.Status, failure_reason = memoryService.GetTurn(userId, msg.TurnId)?.FailureReason, scope = msg.Scope,
                is_deleted    = msg.IsDeleted,
                is_summarized = msg.IsSummarized,
                metadata_json = msg.MetadataJson,
                created_at    = msg.CreatedAt
            }));
        });

        nya.MapGet("/souls", (HttpContext ctx, LocalAdminAuthService auth, NyaSoulProvider soulProvider) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            var souls = soulProvider.ListSouls();
            return Results.Ok(ApiResponseHelper.Success(new
            {
                total = souls.Count,
                souls = souls.Select(s => new { soul_id = s.Id, display_name = s.DisplayName })
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
                souls = souls.Select(s => new { soul_id = s.Id, display_name = s.DisplayName })
            }));
        });

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
                return Results.Conflict(ApiResponseHelper.Error($"Soul '{soul.SoulId}' already exists. Use PUT to update.", 409));

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

            var topicId = ctx.Request.Query["topic_id"].ToString();
            if (string.IsNullOrWhiteSpace(topicId)) topicId = null;
            var unassigned = bool.TryParse(ctx.Request.Query["unassigned"], out var onlyUnassigned) && onlyUnassigned;
            var messages = memoryService.GetAllMessages(userId, limit, offset, topicId, unassigned);
            var total = memoryService.CountAllMessages(userId, topicId, unassigned);
            var diagnostics = memoryService.DiagnoseMessages(userId, messages, CurrentHistoryIds(ctx, userId));

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
                    origin_topic_id = m.OriginTopicId, turn_id = m.TurnId, turn_status = memoryService.GetTurn(userId, m.TurnId)?.Status, failure_reason = memoryService.GetTurn(userId, m.TurnId)?.FailureReason, scope = m.Scope,
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

            var topics = memoryService.ListTopicsWithCount(userId);
            var topicTitles = topics.ToDictionary(t => t.TopicId, t => t.Title);
            var invalidSummaries = memoryService.InvalidSummaryIds(userId);

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
                    source_message_ids = JsonSerializer.Deserialize<string[]>(s.SourceMessageIdsJson),
                    visibility_version = s.VisibilityVersion, scope = s.Scope, conversation_id = s.ConversationId,
                    invalidated = invalidSummaries.Contains(s.SummaryId),
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
            var summaries = memoryService.DeleteAllSummaries(userId);
            var topics = memoryService.DeleteAllTopics(userId);
            var notes = memoryService.ClearTurnMemory(userId);
            auditLogger.LogMemoryDelete(userId, "all");

            return Results.Ok(ApiResponseHelper.Success(new
            {
                user_id = userId,
                soft_deleted_messages = msgs,
                deleted_summaries = summaries,
                deleted_topics = topics,
                deleted_note_versions = notes
            }));
        });

        nya.MapGet("/llm/param-catalog", (
            HttpContext ctx,
            LocalAdminAuthService auth) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            return Results.Ok(ApiResponseHelper.Success(SnakeCase(NyaLlmParamCatalog.All)));
        });

        nya.MapGet("/llm/templates", (
            HttpContext ctx,
            LocalAdminAuthService auth) =>
        {
            if (!auth.TryRequireAuthenticated(ctx, out _, out var denied))
                return denied;

            return Results.Ok(ApiResponseHelper.Success(SnakeCase(NyaLlmProviderTemplates.All)));
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
                api_key = NyaSecretMask.Mask(e.ApiKey), model = e.Model, template = e.Template, params_json = e.ParamsJson, extra_body = e.ToProfile().ExtraBody,
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
            if (body.TryGetProperty("extra_body", out var extraBody))
            {
                if (extraBody.ValueKind != JsonValueKind.Object) return Results.BadRequest(ApiResponseHelper.Error("extra_body 必須是 JSON object。"));
                try
                {
                    var parameters = System.Text.Json.Nodes.JsonNode.Parse(entry.ParamsJson) as System.Text.Json.Nodes.JsonObject;
                    if (parameters == null) return Results.BadRequest(ApiResponseHelper.Error("params_json 必須是 JSON object。"));
                    parameters["extra_body"] = System.Text.Json.Nodes.JsonNode.Parse(extraBody.GetRawText());
                    entry.ParamsJson = parameters.ToJsonString();
                }
                catch (JsonException) { return Results.BadRequest(ApiResponseHelper.Error("params_json 必須是有效 JSON。")); }
            }
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
            return ok ? Results.Ok(ApiResponseHelper.Success(new { profile_id = id, deleted = true })) : Results.NotFound(ApiResponseHelper.Error("not found", 404));
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

    private static void MapMemoryTools(RouteGroupBuilder nya)
    {
        static int PageNumber(HttpContext ctx, string name, int fallback, int max)
            => int.TryParse(ctx.Request.Query[name], out var value) ? Math.Clamp(value, name == "limit" ? 1 : 0, max) : fallback;
        static NyaToolContext Context(HttpContext ctx, string userId, string tool, string arguments, bool budget = false, string? forcedScope = null, string? forcedConversation = null)
        {
            var body = JsonDocument.Parse(arguments);
            using (body)
            {
                var scope = forcedScope ?? ctx.Request.Query["scope"].ToString();
                var external = forcedConversation ?? ctx.Request.Query["conversation_id"].ToString();
                if (scope.Length == 0) scope = "normal";
                if (scope is not ("normal" or "external") || scope == "external" && string.IsNullOrWhiteSpace(external))
                    throw new NyaMemoryException("scope", "scope", "外部範圍須提供 conversation_id。");
                var memory = ctx.RequestServices.GetRequiredService<NyaMemoryService>();
                var topic = scope == "external" ? external : memory.GetActiveTopic(userId)?.TopicId ?? "";
                int? noteBudget = null;
                var estimator = ctx.RequestServices.GetRequiredService<NyaTokenEstimator>();
                if (budget)
                {
                    var config = ctx.RequestServices.GetRequiredService<INyaConfigStore<NyaChatConfig>>().Current;
                    var profile = ctx.RequestServices.GetRequiredService<NyaLlmClient>().ResolveProfile(NyaLlmTasks.Chat);
                    var maxContext = profile.MaxContextTokens ?? config.DefaultMaxContextTokens;
                    var reserve = Math.Clamp((int)(maxContext * config.OutputReserveRatio), config.MinOutputTokens, config.MaxOutputTokens);
                    var registry = ctx.RequestServices.GetRequiredService<NyaToolRegistry>();
                    var tools = registry.BuildLlmTools(userId, userId.Split(':')[0]);

                    var prompt = ctx.RequestServices.GetRequiredService<NyaPromptBuilder>();
                    prompt.BuildTurn(new NyaTurn { TurnId = "admin-budget", UserId = userId, CurrentTopicId = topic, Scope = scope, ConversationId = scope == "external" ? external : null }, memory,
                        ctx.RequestServices.GetRequiredService<NyaSoulProvider>().GetSoul(userId), config.MaxToolRounds,
                        maxContext - reserve - config.ContextSafetyMargin - estimator.Estimate(NyaLlmSerialization.BuildToolsJson(tools).ToJsonString()), estimator, userId.Split(':')[0], out _, out var available);
                    noteBudget = available;
                }
                return new NyaToolContext { ToolName = tool, UserId = userId, ChannelType = userId.Split(':')[0], Scope = scope,
                    ExternalConversationId = scope == "external" ? external : null, ConversationId = topic, ArgumentsJson = arguments, NoteTokenBudget = noteBudget, EstimateTokens = estimator.Estimate };
            }
        }
        static IResult ToolResponse(NyaToolResult result)
            => result.Success ? Results.Ok(ApiResponseHelper.Success(JsonSerializer.Deserialize<JsonElement>(result.LlmContent)))
                : Results.BadRequest(new { success = false, message = JsonDocument.Parse(result.LlmContent).RootElement.GetProperty("error").GetProperty("message").GetString(), data = JsonSerializer.Deserialize<JsonElement>(result.LlmContent) });
        nya.MapGet("/users/{userId}/notes", (HttpContext ctx, string userId, NyaMemoryService memory, INyaConfigStore<NyaChatConfig> config) =>
        {
            var notes = memory.AdminNotes(userId);
            var hidden = memory.GetHideReferences(userId).Where(r => r.IsActive && r.Kind is "note" or "note_version").Select(r => r.TargetId).ToHashSet();
            var limit = PageNumber(ctx, "limit", 50, 100);
            var offset = PageNumber(ctx, "offset", 0, int.MaxValue);
            return Results.Ok(ApiResponseHelper.Success(new { total = notes.Count, limit, offset, capacity = config.Current.NoteCapacity, max_length = config.Current.NoteMaxLength,
                notes = notes.Skip(offset).Take(limit).Select(n => new { note_id = n.NoteId, key = n.Key, content = n.Content, version = n.Version, is_current = n.IsCurrent,
                    scope = n.Scope, conversation_id = n.ConversationId, expires_at = n.ExpiresAt, expired = n.ExpiresAt <= DateTime.UtcNow,
                    hidden = hidden.Contains(n.NoteId) || hidden.Contains(n.VersionId), source_message_ids = JsonSerializer.Deserialize<string[]>(n.SourceMessageIdsJson), created_at = n.CreatedAt }) }));
        });
        nya.MapPost("/users/{userId}/notes", async (HttpContext ctx, string userId, FactToolPlugin plugin) =>
            ToolResponse(await plugin.ExecuteAsync(Context(ctx, userId, "note_save", RequestBodyHelper.GetBody(ctx).GetRawText(), true), ctx.RequestAborted)));
        nya.MapPost("/users/{userId}/notes/{noteId}/restore", (HttpContext ctx, string userId, string noteId, NyaMemoryService memory) =>
        {
            var body = RequestBodyHelper.GetBody(ctx);
            if (!body.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number))
                return Results.BadRequest(ApiResponseHelper.Error("version 須為整數。"));
            var note = memory.RestoreNote(Context(ctx, userId, "note_save", "{}", true), noteId, number);
            ctx.RequestServices.GetRequiredService<NyaAuditLogger>().LogMemoryTool(userId, "note_restore", body.GetRawText(), JsonSerializer.Serialize(new { note_id = note.NoteId, version = note.Version }));
            return ToolResponse(NyaToolResult.Envelope(new { note_id = note.NoteId, version = note.Version, expires_at = note.ExpiresAt }));
        });
        nya.MapPost("/users/{userId}/forget-find", async (HttpContext ctx, string userId, FactToolPlugin plugin) =>
            ToolResponse(await plugin.ExecuteAsync(Context(ctx, userId, "forget_find", RequestBodyHelper.GetBody(ctx).GetRawText()), ctx.RequestAborted)));
        nya.MapPost("/users/{userId}/forget", async (HttpContext ctx, string userId, FactToolPlugin plugin) =>
            ToolResponse(await plugin.ExecuteAsync(Context(ctx, userId, "forget", RequestBodyHelper.GetBody(ctx).GetRawText()), ctx.RequestAborted)));
        nya.MapGet("/users/{userId}/hide-references", (string userId, NyaMemoryService memory) =>
            Results.Ok(ApiResponseHelper.Success(new { references = SnakeCase(memory.GetHideReferences(userId)) })));
        nya.MapPost("/users/{userId}/hide-references/{forgetId}/unforget", (HttpContext ctx, string userId, string forgetId, NyaMemoryService memory) =>
        {
            var reference = memory.GetHideReferences(userId).FirstOrDefault(r => r.ForgetId == forgetId && r.IsActive);
            if (reference == null) return Results.Ok(ApiResponseHelper.Success(new { changed = false }));
            var context = Context(ctx, userId, "forget", "{}", true, reference.Scope, reference.ConversationId);
            var changed = memory.Unforget(userId, forgetId, context.NoteTokenBudget, context.EstimateTokens);
            ctx.RequestServices.GetRequiredService<NyaAuditLogger>().LogMemoryTool(userId, "unforget", JsonSerializer.Serialize(new { forget_id = forgetId }), JsonSerializer.Serialize(new { changed }));
            return Results.Ok(ApiResponseHelper.Success(new { changed }));
        });
        nya.MapGet("/users/{userId}/selections", (HttpContext ctx, string userId, NyaMemoryService memory) =>
            Results.Ok(ApiResponseHelper.Success(new { selections = SnakeCase(memory.AdminSelections(userId, PageNumber(ctx, "limit", 50, 100), PageNumber(ctx, "offset", 0, int.MaxValue))) })));
        nya.MapGet("/users/{userId}/turns", (HttpContext ctx, string userId, NyaMemoryService memory) =>
            Results.Ok(ApiResponseHelper.Success(new { turns = SnakeCase(memory.ListTurns(userId, PageNumber(ctx, "limit", 50, 100), PageNumber(ctx, "offset", 0, int.MaxValue))) })));
        nya.MapGet("/users/{userId}/turns/{turnId}/work", (string userId, string turnId, NyaMemoryService memory) =>
            memory.GetTurn(userId, turnId) == null ? Results.NotFound(ApiResponseHelper.Error($"Turn '{turnId}' not found for user.", 404)) : Results.Ok(ApiResponseHelper.Success(new { work = SnakeCase(memory.GetTurnWork(turnId)) })));
    }

    private static readonly JsonSerializerOptions SnakeCaseOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly JsonSerializerOptions PascalCaseOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = null };

    private static JsonElement SnakeCase<T>(T value) => JsonSerializer.SerializeToElement(value, SnakeCaseOptions);

    private static JsonElement PascalCase<T>(T value) => JsonSerializer.SerializeToElement(value, PascalCaseOptions);

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
                     ["users", _, "overview" or "messages" or "topics" or "summaries" or "notes" or "hide-references" or "selections" or "turns"] or
                     ["users", _, "messages", _] or ["users", _, "turns", _, "work"] or
                     ["users", _, "notes"])
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
        else if (verb == HttpMethods.Post && segments is ["users", _, "notes" or "forget-find" or "forget"] or ["users", _, "notes", _, "restore"] or ["users", _, "hide-references", _, "unforget"])
        {
            permissions = new[] { LocalAdminPermissions.PermissionUserManage };
        }
        else if ((verb == HttpMethods.Delete && segments is ["users", _] or ["users", _, "soul"]) ||
                 ((verb is "POST" or "PUT" or "DELETE") && segments is
                     ["users", _, "messages" or "topics" or "summaries" or "soul"] or
                     ["users", _, "messages" or "topics", _] or
                     ["users", _, "topics", _, "switch" or "rename"] or
                     ["users", _, "summaries", "trigger"] or
                     ["users", _, "summaries", _, "rollback"]))
        {
            permissions = new[] { LocalAdminPermissions.PermissionUserManage };
        }

        return permissions.Length > 0;
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
    private static HashSet<string> CurrentHistoryIds(HttpContext ctx, string userId)
    {
        var memory = ctx.RequestServices.GetRequiredService<NyaMemoryService>();
        var topic = memory.GetActiveTopic(userId);
        if (topic == null) return [];
        var config = ctx.RequestServices.GetRequiredService<INyaConfigStore<NyaChatConfig>>().Current;
        var profile = ctx.RequestServices.GetRequiredService<NyaLlmClient>().ResolveProfile(NyaLlmTasks.Chat);
        var estimator = ctx.RequestServices.GetRequiredService<NyaTokenEstimator>();
        var tools = ctx.RequestServices.GetRequiredService<NyaToolRegistry>().BuildLlmTools(userId, userId.Split(':')[0]);
        var maxContext = profile.MaxContextTokens ?? config.DefaultMaxContextTokens;
        var reserve = Math.Clamp((int)(maxContext * config.OutputReserveRatio), config.MinOutputTokens, config.MaxOutputTokens);
        try
        {
            ctx.RequestServices.GetRequiredService<NyaPromptBuilder>().BuildTurn(
                new NyaTurn { TurnId = "admin-preview", UserId = userId, CurrentTopicId = topic.TopicId }, memory,
                ctx.RequestServices.GetRequiredService<NyaSoulProvider>().GetSoul(userId), config.MaxToolRounds,
                maxContext - reserve - config.ContextSafetyMargin - estimator.Estimate(NyaLlmSerialization.BuildToolsJson(tools).ToJsonString()),
                estimator, userId.Split(':')[0], out var ids, out _);
            return ids;
        }
        catch (NyaMemoryException) { return []; }
    }

}
