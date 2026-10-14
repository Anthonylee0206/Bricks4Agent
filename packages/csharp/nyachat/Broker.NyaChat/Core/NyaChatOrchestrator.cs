using System.Diagnostics;
using System.Text.Json;
using Abstractions = Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

public class NyaChatOrchestrator : Abstractions.INyaChatOrchestrator
{
    private readonly Abstractions.INyaConfigStore<NyaChatConfig> _configStore;
    private NyaChatConfig _config => _configStore.Current;
    private readonly NyaMemoryService _memoryService;
    private readonly NyaSoulProvider _soulProvider;
    private readonly NyaPromptBuilder _promptBuilder;
    private readonly NyaLlmClient _llmClient;
    private readonly NyaReplyPostProcessor _postProcessor;
    private readonly NyaSummarizer _summarizer;
    private readonly NyaAuditLogger _auditLogger;
    private readonly NyaToolRegistry _toolRegistry;
    private readonly NyaTokenEstimator _tokenEstimator;
    private readonly Abstractions.INyaPreChatCommandHandler _preChatCommand;
    private readonly ILogger<NyaChatOrchestrator> _logger;

    public bool IsEnabled => _config.Enabled;

    public NyaChatOrchestrator(Abstractions.INyaConfigStore<NyaChatConfig> configStore, NyaMemoryService memoryService,
        NyaSoulProvider soulProvider, NyaPromptBuilder promptBuilder, NyaLlmClient llmClient, NyaReplyPostProcessor postProcessor,
        NyaSummarizer summarizer, NyaAuditLogger auditLogger, NyaToolRegistry toolRegistry, NyaTokenEstimator tokenEstimator,
        Abstractions.INyaPreChatCommandHandler preChatCommand, ILogger<NyaChatOrchestrator> logger)
    {
        _configStore = configStore;
        _memoryService = memoryService;
        _soulProvider = soulProvider;
        _promptBuilder = promptBuilder;
        _llmClient = llmClient;
        _postProcessor = postProcessor;
        _summarizer = summarizer;
        _auditLogger = auditLogger;
        _toolRegistry = toolRegistry;
        _tokenEstimator = tokenEstimator;
        _preChatCommand = preChatCommand;
        _logger = logger;
    }

    public async Task<Abstractions.NyaChatResult> ChatAsync(Abstractions.NyaChatRequest request, CancellationToken cancellationToken)
    {
        var userId = NyaUserIdentity.Normalize(request.ChannelType, request.ChannelUserId);
        var result = await ChatCoreAsync(userId, request.Message, request.ChannelType, request.ConversationId, cancellationToken);
        return new Abstractions.NyaChatResult { Replies = result.Replies, Error = result.Error, HistoryCount = result.HistoryCount, TopicId = result.TopicId };
    }

    private async Task<NyaChatResult> ChatCoreAsync(string userId, string message, string channelType, string? conversationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(message)) return ErrorResult("使用者識別與訊息內容不可為空。", "empty_input");
        using var lease = await _memoryService.LockUserAsync(userId, ct);
        var traceId = BrokerCore.IdGen.New("nyat");
        NyaTurn? turn = null;
        var sw = Stopwatch.StartNew();
        var ledger = new List<(NyaToolCall Call, NyaToolExecStatus Status)>();
        try
        {
            conversationId = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId;
            var topicId = conversationId ?? _memoryService.GetOrCreateActiveTopic(userId);
            var command = _preChatCommand.Handle(new Abstractions.NyaPreChatCommandContext { UserId = userId, Message = message, ChannelType = channelType, TopicId = topicId });
            turn = _memoryService.BeginTurn(userId, traceId, topicId, message, conversationId);
            if (command.Handled)
            {
                var replies = command.Replies.SelectMany(r => _postProcessor.Process(r, channelType)).ToList();
                _memoryService.FinishTurn(turn, string.Join("\n", replies));
                return new NyaChatResult { Replies = replies, TopicId = topicId };
            }
            var profile = _llmClient.ResolveProfile(NyaLlmTasks.Chat);
            var maxContext = profile.MaxContextTokens ?? _config.DefaultMaxContextTokens;
            var outputReserve = Math.Clamp((int)(maxContext * _config.OutputReserveRatio), _config.MinOutputTokens, _config.MaxOutputTokens);
            var maxRounds = Math.Max(1, _config.MaxToolRounds);
            var priorLedger = PriorLedger(turn);
            var isRetry = System.Text.RegularExpressions.Regex.IsMatch(message, @"^\s*(?:請|麻煩|可以)?\s*(再試(?:一[次遍])?|重試|再來(?:一[次遍])?|繼續(?:剛才|上一輪)?|retry|try again)(?:看看|嗎)?[。.!！?？\s]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            IReadOnlyList<string> Unbacked(string text) => isRetry
                ? NyaToolReplyPlanner.FindUnbackedActions(text, priorLedger.Concat(ledger).ToList())
                : NyaToolReplyPlanner.FindUnbackedActionsWithPrior(text, ledger, priorLedger);
            var rounds = 0;
            var corrected = false;
            var exactReplies = new List<string>();
            var correctionMessages = new List<NyaLlmMessage>();
            List<string>? directReplies = null;
            string? finalContent = null;
            while (true)
            {
                turn = _memoryService.GetTurn(userId, traceId)!;
                var tools = _toolRegistry.BuildLlmTools(userId, channelType).Where(t => turn.Scope == "normal" ||
                    !_toolRegistry.GetAllSchemas().Any(s => s.Name == t.Function.Name && s.Group == "topic")).ToList();
                if (rounds >= maxRounds) tools.Clear();
                var toolCost = _tokenEstimator.Estimate(NyaLlmSerialization.BuildToolsJson(tools).ToJsonString());
                var inputBudget = Math.Max(0, maxContext - outputReserve - _config.ContextSafetyMargin - toolCost);
                var llmMessages = _promptBuilder.BuildTurn(turn, _memoryService, _soulProvider.GetSoul(userId), maxRounds - rounds,
                    inputBudget - _tokenEstimator.Estimate(correctionMessages), _tokenEstimator, channelType, out var contextIds, out var noteBudget);
                llmMessages.AddRange(correctionMessages);
                if (rounds == 0 && !corrected) _auditLogger.LogChatInput(userId, message, profile.Model, _memoryService.ListNotes(userId, turn.Scope, turn.ConversationId).Count,
                    0, contextIds.Count, llmMessages.Count, _tokenEstimator.Estimate(llmMessages), maxContext, false, traceId: traceId,
                    selection: new { topic_id = turn.CurrentTopicId,
                        history = contextIds.Select(id => _memoryService.GetMessage(userId, id)).Where(m => m != null).Select(m => new { message_id = m!.MessageId, content = m.Content, role = m.Role }),
                        notes = _memoryService.ListNotes(userId, turn.Scope, turn.ConversationId).Select(n => new { note_id = n.NoteId, key = n.Key, version = n.Version, value = n.Content }),
                        summaries = Array.Empty<object>(), unavailable_tools = _toolRegistry.GetUnavailableSchemas(userId, channelType).Select(t => new { name = t.Schema.Name, reason = t.Reason }) },
                    execution: new { channel_type = channelType, conversation_id = conversationId, profile = new { id = profile.ProfileId, provider = profile.Provider, model = profile.Model }, function_tools = tools.Select(t => t.Function.Name).ToArray(), initial_messages = llmMessages.Select(m => new { role = m.Role, content = m.Content }).ToArray() });
                var response = await _llmClient.ChatAsync(new NyaLlmRequest
                {
                    AuditTraceId = traceId, AuditUserId = userId, Messages = llmMessages, Tools = tools.Count == 0 ? null : tools, MaxTokens = outputReserve
                }, ct);
                if (response == null) throw new NyaMemoryException(NyaLlmReasons.LlmUnavailable, "reply", "AI 服務暫時無法回應，請稍後再試。");
                if (response.HasToolCalls)
                {
                    if (rounds >= maxRounds)
                    {
                        finalContent = "本輪工具回合已用完。" + NyaToolStatusFormatter.FormatFallback(ledger.Select(e => e.Status));
                        break;
                    }
                    rounds++;
                    correctionMessages.Clear();
                    var results = new List<Abstractions.NyaToolResult>();
                    var budgetDirty = true;
                    foreach (var call in response.ToolCalls!)
                    {
                        turn = _memoryService.GetTurn(userId, traceId)!;
                        if (budgetDirty) _promptBuilder.BuildTurn(turn, _memoryService, _soulProvider.GetSoul(userId), maxRounds - rounds,
                            inputBudget, _tokenEstimator, channelType, out _, out noteBudget);
                        var visibility = _memoryService.VisibilityVersion(userId);
                        _auditLogger.LogToolCall(userId, call.FunctionName, call.FunctionArguments, "tool", traceId: traceId);
                        var result = await ExecuteToolAsync(call, turn, channelType, contextIds, noteBudget, ct);
                        if (result.Effects.Count == 0 && _tokenEstimator.Estimate(call.FunctionArguments + result.LlmContent) + 32 >
                            noteBudget - NyaMemoryService.NoteSnapshotTokens(_memoryService.ListNotes(userId, turn.Scope, turn.ConversationId), _tokenEstimator.Estimate))
                            result = Abstractions.NyaToolResult.Error("result_capacity", "query", "工具結果超過本輪上下文容量，請縮小查詢範圍、減少 limit 或分段讀取。");
                        var status = NyaToolStatusFormatter.FromResult(result);
                        ledger.Add((call, status));
                        results.Add(result);
                        var dependencies = contextIds.Concat(_memoryService.ListNotes(userId, turn.Scope, turn.ConversationId).Select(n => n.NoteId))
                            .Concat(ReferencedIds(call.FunctionArguments)).Concat(ReferencedIds(result.LlmContent));
                        var workResult = result.RequiresExactOutput && result.DirectReplies is { Count: > 0 }
                            ? Abstractions.NyaToolResult.Envelope(new { output = "此工具的輸出將由系統原樣提供，請勿重述或改寫。" }, result.Outcome, result.Effects)
                            : result;
                        _memoryService.SaveTurnWork(turn, call, workResult, dependencies);
                        budgetDirty = result.Effects.Count > 0;
                        if (!budgetDirty) noteBudget = Math.Max(0, noteBudget - _tokenEstimator.Estimate(call.FunctionArguments + workResult.LlmContent) - 32);
                        _auditLogger.LogToolResult(userId, call.FunctionName, result.LlmContent, result.Success ? "success" : "error", traceId: traceId);
                        if (call.FunctionName == "forget" && _memoryService.VisibilityVersion(userId) != visibility)
                        {
                            exactReplies.Clear();
                            correctionMessages.Clear();
                        }
                        if (result.RequiresExactOutput && result.DirectReplies is { Count: > 0 }) exactReplies.AddRange(result.DirectReplies);
                    }
                    directReplies = NyaToolReplyPlanner.PlanDirectReplies(results)?.ToList();
                    if (directReplies != null)
                    {
                        var unbackedDirect = Unbacked(string.Join("\n", directReplies.SelectMany(r => _postProcessor.Process(r, "grounding"))));
                        if (unbackedDirect.Count == 0) break;
                        directReplies = null;
                        corrected = true;
                        correctionMessages.Add(new NyaLlmMessage { Role = "system", Content = "工具原樣輸出包含未佐證宣稱，請據實改寫，不得宣稱完成：" + string.Join("、", unbackedDirect) });
                    }
                    continue;
                }
                finalContent = response.Content;
                if (string.IsNullOrWhiteSpace(finalContent)) throw new NyaMemoryException(NyaLlmReasons.LlmEmpty, "reply", "AI 服務回覆為空，請稍後再試。");
                var unbacked = Unbacked(string.Join("\n", _postProcessor.Process(finalContent, "grounding")));
                if (unbacked.Count == 0) break;
                if (corrected)
                {
                    finalContent = "我還不能確認這些操作已完成。" + NyaToolStatusFormatter.FormatFallback(ledger.Select(e => e.Status));
                    break;
                }
                corrected = true;
                correctionMessages.Add(new NyaLlmMessage { Role = "assistant", Content = finalContent });
                correctionMessages.Add(new NyaLlmMessage { Role = "system", Content = $"本輪沒有成功執行以下動作：{string.Join("、", unbacked)}。請呼叫工具或改寫回覆，不要宣稱完成；本輪工具回合硬上限仍適用。" });
            }
            turn = _memoryService.GetTurn(userId, traceId)!;
            var processed = (directReplies ?? new List<string> { finalContent ?? "" }).SelectMany(r => _postProcessor.Process(r, channelType)).ToList();
            if (directReplies == null) processed.AddRange(exactReplies.SelectMany(r => _postProcessor.Process(r, channelType)));
            if (processed.All(string.IsNullOrWhiteSpace)) throw new NyaMemoryException(NyaLlmReasons.LlmEmpty, "reply", "AI 服務回覆為空，請稍後再試。");
            _memoryService.FinishTurn(turn, string.Join("\n", processed));
            _auditLogger.LogChatOutput(userId, string.Join("\n", processed), profile.Model, sw.ElapsedMilliseconds, processed.Count, traceId: traceId);
            if (_config.AutoSummarizeEnabled) _summarizer.Queue(userId, turn.CurrentTopicId, turn.Scope, turn.ConversationId);
            return new NyaChatResult { Replies = processed, TopicId = turn.CurrentTopicId, HistoryCount = _memoryService.CountActiveMessages(userId, turn.CurrentTopicId) };
        }
        catch (Exception ex)
        {
            if (turn != null && _memoryService.GetTurn(userId, traceId)?.Status == "pending")
            {
                turn = _memoryService.GetTurn(userId, traceId)!;
                _memoryService.FinishTurn(turn, null, ex is NyaMemoryException memoryError ? memoryError.Code : ex is OperationCanceledException ? "cancelled" : "chat_error");
            }
            _logger.LogWarning(ex, "[NyaChat] Turn {Turn} failed", traceId);
            _auditLogger.LogError(userId, "chat", ex.ToString(), traceId: traceId);
            var result = ErrorResult(ex is NyaMemoryException error ? error.Message : "AI 服務暫時無法回應，請稍後再試。", ex is NyaMemoryException reason ? reason.Code : "chat_error");
            if (ledger.Count > 0) result.Replies[0] += "\n" + NyaToolStatusFormatter.FormatFallback(ledger.Select(e => e.Status));
            result.TopicId = turn?.CurrentTopicId;
            return result;
        }
    }

    private async Task<Abstractions.NyaToolResult> ExecuteToolAsync(NyaToolCall call, NyaTurn turn, string channelType, HashSet<string> contextIds, int noteBudget, CancellationToken ct)
    {
        if (!_toolRegistry.TryGetExecutablePlugin(call.FunctionName, turn.UserId, channelType, out var plugin, out var schema, out var denied))
            return Abstractions.NyaToolResult.Error(denied!, "tool", denied == "not_authorized" ? "沒有權限。" : denied == "disabled" ? "工具已停用。" : "未知工具。");
        if (turn.Scope == "external" && schema.Group == "topic") return Abstractions.NyaToolResult.Error("external_scope", "scope", "外部明確會話不能操作聊天室。");
        var validation = NyaToolArgValidator.ValidateError(schema, call.FunctionArguments);
        if (validation != null) return Abstractions.NyaToolResult.Error("invalid_args", validation.Field, validation.Message);
        var context = new Abstractions.NyaToolContext
        {
            ToolName = call.FunctionName, TraceId = turn.TurnId, UserId = turn.UserId, ChannelType = channelType,
            ConversationId = turn.CurrentTopicId, Scope = turn.Scope, ExternalConversationId = turn.ConversationId,
            CurrentContextMessageIds = contextIds, NoteTokenBudget = Math.Max(0, noteBudget - _tokenEstimator.Estimate(call.FunctionArguments) * 2 - 1024), ArgumentsJson = call.FunctionArguments,
            EstimateTokens = _tokenEstimator.Estimate
        };
        try
        {
            return await NyaToolTimeout.RunAsync(plugin, context, TimeSpan.FromSeconds(Math.Max(1, _config.ToolExecTimeoutSeconds)), ct)
                ?? Abstractions.NyaToolResult.Error("timeout", "tool", "工具執行逾時。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _auditLogger.LogError(turn.UserId, "tool_exec:" + call.FunctionName, ex.ToString(), turn.TurnId);
            return ex is NyaMemoryException error ? Abstractions.NyaToolResult.Error(error.Code, error.Field, error.Message)
                : Abstractions.NyaToolResult.Error("internal_error", "tool", "工具執行失敗。");
        }
    }

    private static IEnumerable<string> ReferencedIds(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var ids = new List<string>();
            void Visit(JsonElement node)
            {
                if (node.ValueKind == JsonValueKind.String && node.GetString() is { } value &&
                    (value.StartsWith("nyam_", StringComparison.Ordinal) || value.StartsWith("nyan_", StringComparison.Ordinal))) ids.Add(value);
                else if (node.ValueKind == JsonValueKind.Object) foreach (var property in node.EnumerateObject()) Visit(property.Value);
                else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Visit(item);
            }
            Visit(doc.RootElement);
            return ids;
        }
        catch (JsonException) { return []; }
    }

    private List<(NyaToolCall Call, NyaToolExecStatus Status)> PriorLedger(NyaTurn turn)
    {
        var previous = _memoryService.ListTurns(turn.UserId).FirstOrDefault(t => t.TurnId != turn.TurnId && t.Scope == turn.Scope && t.CurrentTopicId == turn.CurrentTopicId && t.ConversationId == turn.ConversationId);
        if (previous?.Status != "failed") return [];
        var ledger = new List<(NyaToolCall Call, NyaToolExecStatus Status)>();
        foreach (var work in _memoryService.GetTurnWork(previous.TurnId).Where(w => !w.IsHidden))
        {
            using var result = JsonDocument.Parse(work.ResultJson);
            var root = result.RootElement;
            if (work.CallJson != "{}" && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && root.TryGetProperty("outcome", out var outcome) && outcome.GetString() == "completed")
                ledger.Add((JsonSerializer.Deserialize<NyaToolCall>(work.CallJson)!, NyaToolExecStatus.Success));
            if (work.CallJson == "{}")
            {
                using var effects = JsonDocument.Parse(work.EffectsJson);
                foreach (var effect in effects.RootElement.EnumerateArray())
                    if (effect.TryGetProperty("action", out var action) && action.GetString() is "note_save" or "forget" or "new_topic" or "switch_topic" or "rename_topic")
                        ledger.Add((new NyaToolCall { Id = work.WorkId, FunctionName = action.GetString()! }, NyaToolExecStatus.Success));
            }
        }
        return ledger;
    }

    private static NyaChatResult ErrorResult(string message, string error) => new() { Replies = [message], Error = error };
}
