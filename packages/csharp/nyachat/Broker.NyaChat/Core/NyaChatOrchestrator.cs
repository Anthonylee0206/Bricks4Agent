using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Abstractions = Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// NyaChat V2 主協調器：串接所有子服務，實現完整對話流程。
///
/// 同步流程：
///   1. 寫入短期記憶 → 2. 取得/建立 topicId →
///   3. 載入記憶與事實 → 4. 取得 Soul → 5. 組裝 prompt →
///   6. 記錄輸入 → 7. 呼叫 LLM（含 function calling） →
///   8. 處理 tool_calls（統一閉環：查 plugin → 授權 → 執行 → 二次 LLM） →
///   9. 後處理回覆 → 10. 寫入助理回覆 → 11. 記錄輸出
///
/// 非同步背景（不影響回覆時間）：
///   12. 摘要壓縮
///
/// Tool calling 架構（大項四）：
///   - 所有工具（事實 / 搜尋 / 交通 / 未來量化…）皆為 <c>INyaToolPlugin</c>，走<b>同一條閉環</b>：
///     單輪內「並行」執行全部 tool_calls（審查5 + 改進2）→ 把結果以 role=tool 追加 →
///     後續 LLM 呼叫產生個性化回覆或續查工具（改進1：有界多輪，上限 MaxToolRounds，
///     末輪不帶 tools 防遞迴）。每個工具受 ToolExecTimeoutSeconds 時限（改進3）。
///   - D2（Bug #7 修復）：插件可回傳 <c>NyaToolResult.DirectReplies</c> 選擇「維持原樣輸出（worker 結果）」。
///     當本輪**全部**工具均要求原樣輸出時，依序串接後略過二次 LLM；
///     只要有任一工具走個性化路徑（混合情況），一律整批進閉環二次 LLM。
/// </summary>
public class NyaChatOrchestrator : Abstractions.INyaChatOrchestrator
{
    private readonly Abstractions.INyaConfigStore<NyaChatConfig> _configStore;
    private NyaChatConfig _config => _configStore.Current; // 大項六 6D：每次讀取取得當前快照
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
    private readonly Abstractions.INyaPrincipalResolver _principalResolver;
    private readonly ILogger<NyaChatOrchestrator> _logger;

    // 大項七 Fix B：per-user 背景摘要進行中旗標，避免同一使用者並發跑多個摘要任務
    //（重複摘要 / 序號範圍交疊 / 與 AppendMessage 競爭）。Orchestrator 為 Singleton，故此為行程內全域共享。
    private readonly ConcurrentDictionary<string, byte> _summarizing = new();

    public bool IsEnabled => _config.Enabled;

    public NyaChatOrchestrator(
        Abstractions.INyaConfigStore<NyaChatConfig> configStore,
        NyaMemoryService memoryService,
        NyaSoulProvider soulProvider,
        NyaPromptBuilder promptBuilder,
        NyaLlmClient llmClient,
        NyaReplyPostProcessor postProcessor,
        NyaSummarizer summarizer,
        NyaAuditLogger auditLogger,
        NyaToolRegistry toolRegistry,
        NyaTokenEstimator tokenEstimator,
        Abstractions.INyaPreChatCommandHandler preChatCommand,
        Abstractions.INyaPrincipalResolver principalResolver,
        ILogger<NyaChatOrchestrator> logger)
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
        _principalResolver = principalResolver;
        _logger = logger;
    }

    /// <summary>
    /// 通道無關入口（<see cref="Abstractions.INyaChatOrchestrator"/>，大項一）。
    /// 以 (ChannelType, ChannelUserId) 正規化 userId、honor ConversationId，依通道 profile
    /// 格式化回覆，並自行完成<b>所有</b>工具的「工具 → 結果 → LLM 二次回覆」閉環（大項四）。
    /// </summary>
    public async Task<Abstractions.NyaChatResult> ChatAsync(
        Abstractions.NyaChatRequest request,
        CancellationToken cancellationToken)
    {
        var userId = NyaUserIdentity.Normalize(request.ChannelType, request.ChannelUserId);
        var core = await ChatCoreAsync(userId, request.Message, request.ChannelType, request.ConversationId, cancellationToken);
        return new Abstractions.NyaChatResult
        {
            Replies      = core.Replies,
            Error        = core.Error,
            HistoryCount = core.HistoryCount,
            TopicId      = core.TopicId
        };
    }

    /// <summary>
    /// 對話核心流程（通道無關）。userId 已正規化；channelType 決定回覆格式化 profile；
    /// conversationId 非空時作為話題 ID，否則使用該使用者當前 active 話題。
    /// 所有工具（含外部工具）的執行與二次回覆閉環皆在此完成，<c>Replies</c> 即最終回覆。
    /// </summary>
    private async Task<NyaChatResult> ChatCoreAsync(
        string userId,
        string message,
        string channelType,
        string? conversationId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(message))
            return ErrorResult("使用者識別與訊息內容不可為空。", "empty_input");

        // ── 0. Guest gate（2026-07-08）：無 principal 綁定 → 靜默記錄，不進 LLM、不回覆 ──
        // fail-closed：resolver 例外視為 guest（授權查不到就不放行）。
        bool authorized;
        try { authorized = _principalResolver.IsAuthorized(userId, channelType); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaChat] principal resolve failed for {User}; treating as guest",
                userId[..Math.Min(8, userId.Length)]);
            authorized = false;
        }
        if (!authorized)
        {
            try
            {
                var guestTopic = !string.IsNullOrWhiteSpace(conversationId)
                    ? conversationId!
                    : _memoryService.GetOrCreateActiveTopic(userId);
                _memoryService.AppendMessage(userId, "user", message, guestTopic);
                _auditLogger.LogGuestBlocked(userId, channelType);
                return new NyaChatResult { Replies = new List<string>(), TopicId = guestTopic, HistoryCount = 0 };
            }
            catch (Exception ex)
            {
                // 記錄失敗也維持靜默——不能因落地失敗而放行或回錯誤訊息給陌生人。
                _logger.LogError(ex, "[NyaChat] guest logging failed for {User}", userId[..Math.Min(8, userId.Length)]);
                return new NyaChatResult { Replies = new List<string>(), HistoryCount = 0 };
            }
        }

        var sw = Stopwatch.StartNew();

        try
        {
            // ── 1. 取得話題：conversationId 優先，否則目前 active 話題（DB 持久化）──
            var topicId = !string.IsNullOrWhiteSpace(conversationId)
                ? conversationId!
                : _memoryService.GetOrCreateActiveTopic(userId);

            // Phase 4-A：本輪全鏈路關聯 ID。串進所有稽核項與工具 context，使「對話→工具→審批」可反查。
            var traceId = BrokerCore.IdGen.New("nyat");

            // ── 1.5 字面指令攔截（大項#4）：命中則短路，不進 LLM、不寫入歷史 ──
            var command = _preChatCommand.Handle(new Abstractions.NyaPreChatCommandContext
            {
                UserId = userId, Message = message, ChannelType = channelType, TopicId = topicId
            });
            if (command.Handled)
            {
                var cmdReplies = command.Replies
                    .SelectMany(r => _postProcessor.Process(r, channelType)).ToList();
                if (cmdReplies.Count == 0) cmdReplies.Add("");
                return new NyaChatResult { Replies = cmdReplies, TopicId = topicId, HistoryCount = 0 };
            }

            // ── 2. 載入記憶層 ─────────────────────────────────────────────────
            // 大項#3：對話與摘要依話題獨立（事實 GetActiveFacts 維持跨話題共用）。
            // 注意：使用者訊息延後到 LLM 成功回應後才連同助理回覆一起寫入（步驟 10）。
            // 若 LLM 請求失敗（回 null / 例外），整輪不落地，避免未獲回覆的 user 訊息
            // 累積成連續 user turn（防止用戶連續對話 / 歷史污染）。
            var activeFacts      = _memoryService.GetActiveFacts(userId);
            var activeSummaries  = _memoryService.GetActiveSummaries(userId, topicId);
            var recentMessages   = _memoryService.GetRecentMessages(userId, topicId, _config.ShortTermMessageLimit);
            // 當前訊息尚未持久化，recentMessages 本就不含它，無需再過濾。
            var historyMessages  = recentMessages;

            // ── 4. 取得 Soul ──────────────────────────────────────────────────
            var soul = _soulProvider.GetSoul(userId);

            // 大項二：解析對話 profile，audit / metadata 改記「實際路由後」的 model/provider（避免與 profile 漂移）
            var chatProfile = _llmClient.ResolveProfile(NyaLlmTasks.Chat);

            // ── 5. 計算 token 預算（大項三）並組裝 prompt ─────────────────────
            var maxContext    = chatProfile.MaxContextTokens ?? _config.DefaultMaxContextTokens;
            var outputReserve = Math.Clamp(
                (int)(maxContext * _config.OutputReserveRatio), _config.MinOutputTokens, _config.MaxOutputTokens);
            var inputBudget   = Math.Max(0, maxContext - outputReserve - _config.ContextSafetyMargin);
            var budget = new NyaPromptBudget
            {
                InputTokenBudget = inputBudget,
                FactRatio        = _config.FactTokenBudgetRatio,
                SummaryRatio     = _config.SummaryTokenBudgetRatio
            };
            var availableTopics = _memoryService.ListTopicsWithCount(userId);
            var llmMessages = _promptBuilder.Build(
                message, soul, activeFacts, activeSummaries, historyMessages, budget, _tokenEstimator,
                userId, channelType, availableTopics, topicId, out var budgetReport);

            // ── 6. 記錄輸入（含 token 估算與截斷統計，大項三 3H）──────────────
            _auditLogger.LogChatInput(userId, message, chatProfile.Model,
                budgetReport.FactsIncluded, budgetReport.SummariesIncluded, budgetReport.HistoryIncluded,
                llmMessages.Count, budgetReport.EstimatedTokens, maxContext, budgetReport.Truncated, traceId: traceId);

            if (budgetReport.MandatoryOverflow)
                _logger.LogWarning(
                    "[NyaChat] User={User} mandatory facts (instruction/identity) exceed input budget {Budget}; included anyway.",
                    userId[..Math.Min(8, userId.Length)], inputBudget);

            // ── 7. 建構工具清單（啟用 ∩ 授權）並呼叫 LLM ───────────────────────
            var tools = _toolRegistry.BuildLlmTools(userId, channelType);
            var request = new NyaLlmRequest
            {
                // Model 與取樣（Temperature/TopP）由 chat profile 決定，facade 會對齊；此處僅組訊息與工具。
                Messages  = llmMessages,
                Tools     = tools.Count > 0 ? tools : null,
                MaxTokens = outputReserve // 大項三 3E：補足 chat 原本無輸出上限
            };

            var llmResponse = await _llmClient.ChatAsync(request, ct);

            if (llmResponse == null)
            {
                _auditLogger.LogError(userId, "chat", NyaLlmReasons.LlmUnavailable, traceId: traceId);
                return ErrorResult("抱歉，AI 服務暫時無法回應，請稍後再試。", NyaLlmReasons.LlmUnavailable);
            }

            // ── 8. 處理 tool_calls（有界多輪閉環，大項四）────────────────────
            // 改進1：後續 LLM 呼叫改帶 tools（回合數 < MaxToolRounds 時），讓 LLM 能依前輪結果
            // 續查（查A→看結果→查B）。遞迴防護 = 回合計數器：最後一輪不帶 tools，模型只能總結。
            List<string>? directReplies = null;
            var toolRound = 0;
            var directiveAdded = false;

            while (llmResponse.HasToolCalls)
            {
                toolRound++;
                var toolCalls = llmResponse.ToolCalls!;

                // 記錄所有 LLM 決定呼叫的工具
                foreach (var tc in toolCalls)
                    _auditLogger.LogToolCall(userId, tc.FunctionName, tc.FunctionArguments, ResolveToolType(tc.FunctionName), traceId: traceId);

                // 審查5：單輪內執行「全部」工具（不再 FirstOrDefault、不再丟棄）。
                // 改進2：同輪 tool_calls 並行執行（彼此獨立；DB 寫入由 BrokerDb write gate 序列化）。
                // ponytail: 假設插件無跨呼叫共享可變狀態；若未來插件有序依賴，改回逐一 await。
                var outcomes = await Task.WhenAll(
                    toolCalls.Select(tc => ExecuteToolAsync(tc, userId, channelType, topicId, traceId, ct)));

                var executed = new List<(NyaToolCall Call, Abstractions.NyaToolResult Result, NyaToolExecStatus Status)>();
                for (var i = 0; i < toolCalls.Count; i++)
                {
                    var (result, status) = outcomes[i];
                    _auditLogger.LogToolResult(userId, toolCalls[i].FunctionName, Truncate(result.LlmContent, 200),
                        AuditResult(status), traceId: traceId);
                    executed.Add((toolCalls[i], result, status));
                }

                // D2 / Bug #7：DirectReplies 不再只在單一工具時 honor。
                // 全部工具都要求原樣輸出 → 串接全部、略過後續 LLM（多工具下不再靜默丟棄）。
                var results = executed.Select(e => e.Result).ToList();
                var planned = NyaToolReplyPlanner.PlanDirectReplies(results);
                if (planned != null)
                {
                    directReplies = planned.ToList();
                    break;
                }

                if (NyaToolReplyPlanner.IsMixed(results))
                    _logger.LogInformation(
                        "[NyaChat] User={User} mixed direct/personalized tool results in one turn; routing all through closed-loop LLM.",
                        userId[..Math.Min(8, userId.Length)]);

                // 閉環：追加 assistant(tool_calls) + 每筆 role=tool 結果 → 下一次 LLM。
                // 大項三 3D：工具結果可能很長（如搜尋），餵回前依 PerToolResultMaxTokens 截尾，避免撐爆 context。
                llmMessages.Add(new NyaLlmMessage { Role = "assistant", Content = "", ToolCalls = toolCalls });
                foreach (var (tc, result, status) in executed)
                {
                    // 先依 PerToolResultMaxTokens 截尾原始內容，再於最前面標註執行狀態
                    //（成功/失敗/未啟動），狀態標籤不受內容截斷影響——讓 LLM 明確看到工具到底有沒有跑、成不成功。
                    var content = _tokenEstimator.TruncateToTokens(result.LlmContent, _config.PerToolResultMaxTokens);
                    llmMessages.Add(new NyaLlmMessage
                    {
                        Role       = "tool",
                        Content    = NyaToolStatusFormatter.FormatForLlm(status, content),
                        ToolCallId = tc.Id,
                        Name       = tc.FunctionName
                    });
                }

                // Doc 2 surface #2：給後續回覆明確任務層指令（用人格轉述、勿改數字/事實），
                // 避免模型自由發揮竄改/編造工具結果（金融/量化工具尤其重要）。只加一次，防多輪堆疊。
                if (!directiveAdded)
                {
                    llmMessages.Add(new NyaLlmMessage { Role = "system", Content = _promptBuilder.ToolResultDirective() });
                    directiveAdded = true;
                }

                // Model/取樣由 chat profile 對齊；3E：後續 LLM 同樣套用輸出上限
                var allowMoreTools = toolRound < Math.Max(1, _config.MaxToolRounds);
                var followUp = new NyaLlmRequest
                {
                    Messages  = llmMessages,
                    Tools     = allowMoreTools && tools.Count > 0 ? tools : null,
                    MaxTokens = outputReserve
                };
                llmResponse = await _llmClient.ChatAsync(followUp, ct) ?? new NyaLlmResponse { Content = "操作執行完成。" };
                if (!allowMoreTools) break; // 末輪未帶 tools，理論上不會再有 tool_calls；防衛性終止
            }

            // ── 9. 後處理回覆（依通道 profile）────────────────────────────────
            List<string> processedReplies;
            if (directReplies != null)
            {
                // 原樣輸出仍走 PostProcessor 以套用通道格式（多段切割 / markdown 降級）
                processedReplies = directReplies.SelectMany(r => _postProcessor.Process(r, channelType)).ToList();
                if (processedReplies.Count == 0) processedReplies.Add("");
            }
            else
            {
                processedReplies = _postProcessor.Process(llmResponse.Content ?? "", channelType);
            }
            var primaryReply = processedReplies.FirstOrDefault() ?? "";

            // ── 9.5 空白回覆視同失敗（Fix 4）─────────────────────────────────
            // LLM 回非 null 但空白/全空格內容（thinking 模型耗盡輸出預算的 gotcha，這次發生在 chat）。
            // 舊行為：落地一則空白 assistant 訊息並記錄空白 chat_output，看不出是錯誤。
            // 改為比照上方 null 路徑：寫 error 稽核、回 ErrorResult、整輪不落地（不持久化空白訊息）。
            // 限 LLM 回覆路徑（directReplies == null）；工具原樣輸出不在此列。
            if (directReplies == null && string.IsNullOrWhiteSpace(primaryReply))
            {
                _auditLogger.LogError(userId, "chat", NyaLlmReasons.LlmEmpty, traceId: traceId);
                return ErrorResult("抱歉，AI 服務暫時無法回應，請稍後再試。", NyaLlmReasons.LlmEmpty);
            }

            // ── 10. 寫入對話紀錄（延後落地）──────────────────────────────────
            // 至此 LLM 已成功回應；先補寫使用者訊息，再寫助理回覆，維持正確順序。
            // 失敗路徑（步驟 7 回 null、例外）不會走到這裡，因此該輪完全不落地。
            _memoryService.AppendMessage(userId, "user", message, topicId);
            _memoryService.AppendMessage(
                userId, "assistant", primaryReply,
                topicId,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    model       = chatProfile.Model,
                    provider    = chatProfile.Provider,
                    latency_ms  = sw.ElapsedMilliseconds
                }));

            // ── 11. 記錄輸出 ──────────────────────────────────────────────────
            sw.Stop();
            _auditLogger.LogChatOutput(userId, primaryReply, chatProfile.Model, sw.ElapsedMilliseconds, processedReplies.Count, traceId: traceId);

            _logger.LogInformation(
                "[NyaChat] User={User} → reply={Preview}... parts={Parts} latency={Ms}ms",
                userId[..Math.Min(8, userId.Length)],
                Truncate(primaryReply, 60),
                processedReplies.Count,
                sw.ElapsedMilliseconds);

            // ── 12. 非同步背景：摘要壓縮（大項#3：依當前話題）─────────────────
            _ = RunBackgroundTasksAsync(userId, topicId);

            return new NyaChatResult
            {
                Replies      = processedReplies,
                HistoryCount = recentMessages.Count + 2, // 本輪補寫的 user + assistant 兩則
                TopicId      = topicId
            };
        }
        catch (OperationCanceledException)
        {
            _auditLogger.LogError(userId, "chat", "Request cancelled");
            return ErrorResult("抱歉，請求逾時，請稍後再試。", "timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[NyaChat] Unexpected error for user {User}", userId);
            _auditLogger.LogError(userId, "chat", ex.Message);
            return ErrorResult("處理對話時發生錯誤，請稍後再試。", ex.Message);
        }
    }

    // ── 工具執行（統一分派 + 授權，大項四）──────────────────────────────────

    /// <summary>
    /// 執行單一 tool_call：查 plugin → 執行前授權再檢查（第二道關卡）→ 建 context → ExecuteAsync。
    /// 任何失敗皆回傳 <see cref="Abstractions.NyaToolResult.Fail"/>，由閉環餵回 LLM 向使用者說明。
    /// </summary>
    /// <returns>
    /// 結果 + 三態執行狀態（<see cref="NyaToolExecStatus"/>）。狀態用來在閉環 role=tool 前明確標註
    /// 「成功 / 失敗 / 未啟動」，並對齊稽核 result（success / error / skipped），讓 LLM 與稽核都看得到工具到底有沒有跑。
    /// </returns>
    private async Task<(Abstractions.NyaToolResult Result, NyaToolExecStatus Status)> ExecuteToolAsync(
        NyaToolCall tc, string userId, string channelType, string topicId, string traceId, CancellationToken ct)
    {
        // Bug #8：執行門 = 已知 ∩ 已啟用 ∩ 授權（修復「停用工具仍可被執行」的暴露面/執行面不對稱）。
        // 被執行門擋下 = 工具根本沒跑（關閉/無權限/不存在）→ NotEnabled（未啟動）。
        if (!_toolRegistry.TryGetExecutablePlugin(tc.FunctionName, userId, channelType,
                out var plugin, out var schema, out var denyStatus))
        {
            _logger.LogWarning("[NyaChat] Tool {Tool} not executable for user {User}: {Status}",
                tc.FunctionName, userId[..Math.Min(8, userId.Length)], denyStatus);
            return (Abstractions.NyaToolResult.Fail(StatusJson(denyStatus!, StatusMessage(denyStatus!))),
                    NyaToolExecStatus.NotEnabled);
        }

        // Bug #4：執行前依 schema 驗證 LLM 提供的參數（required + enum），失敗回饋給 LLM 使其修正。
        // 工具存在且啟用、只是參數有誤 → 視為「失敗」（有嘗試執行），非「未啟動」。
        var (argsOk, argsError) = NyaToolArgValidator.Validate(schema, tc.FunctionArguments);
        if (!argsOk)
        {
            _logger.LogInformation("[NyaChat] Tool {Tool} argument validation failed: {Err}", tc.FunctionName, argsError);
            return (Abstractions.NyaToolResult.Fail(StatusJson("invalid_args", argsError!)), NyaToolExecStatus.Failed);
        }

        var ctx = new Abstractions.NyaToolContext
        {
            ToolName       = tc.FunctionName,
            TraceId        = traceId,
            UserId         = userId,
            ChannelType    = channelType,
            ConversationId = topicId,
            ArgumentsJson  = tc.FunctionArguments
        };

        try
        {
            // 改進3：per-tool timeout（linked CTS）。慢插件逾時 → 該工具視為失敗，不拖死整輪對話。
            var timeout = TimeSpan.FromSeconds(Math.Max(1, _config.ToolExecTimeoutSeconds));
            var result = await NyaToolTimeout.RunAsync(plugin, ctx, timeout, ct);
            if (result is null)
            {
                _logger.LogWarning("[NyaChat] Tool {Tool} timed out after {Sec}s", tc.FunctionName, timeout.TotalSeconds);
                _auditLogger.LogError(userId, $"tool_exec:{tc.FunctionName}", $"timeout after {timeout.TotalSeconds}s", traceId);
                return (Abstractions.NyaToolResult.Fail(StatusJson("timeout", "工具執行逾時，請稍後再試。")),
                        NyaToolExecStatus.Failed);
            }
            return (result, NyaToolStatusFormatter.FromResult(result));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 整體請求取消：交給 ChatCoreAsync 的 OCE 處理，不 sanitize 成工具失敗
        }
        catch (Exception ex)
        {
            // Bug #10：原始例外只記 server 端;回饋 LLM 的一律泛用訊息,避免 DB/路徑/連線細節外洩。
            _logger.LogWarning(ex, "[NyaChat] Tool {Tool} threw during execution", tc.FunctionName);
            // 真實例外另落地稽核 DB（console 無持久 sink，行程重啟即遺失，事後鑑識查不到真因）。
            // 稽核表僅管理端可讀，不經 LLM，無外洩疑慮。LogError 內部截 300 字。
            _auditLogger.LogError(userId, $"tool_exec:{tc.FunctionName}", ex.ToString(), traceId);
            return (Abstractions.NyaToolResult.Fail(NyaToolError.InternalJson()), NyaToolExecStatus.Failed);
        }
    }

    /// <summary>把執行門狀態碼轉成可轉述給使用者的中文訊息（item 7）。</summary>
    private static string StatusMessage(string status) => status switch
    {
        "disabled"       => "此功能目前已關閉。",
        "not_authorized" => "你沒有使用此功能的權限。",
        "unknown_tool"   => "沒有這個工具。",
        "invalid_args"   => "工具參數有誤。",
        _                => "處理時發生錯誤。"
    };

    /// <summary>結構化狀態 JSON（escape 後內嵌）回饋給 LLM，由閉環轉述（item 7）。</summary>
    private static string StatusJson(string status, string message)
    {
        string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"{{\"success\":false,\"status\":\"{status}\",\"message\":\"{Esc(message)}\"}}";
    }

    /// <summary>把三態執行狀態對齊稽核 result 欄位的既定值（success / error / skipped）。</summary>
    private static string AuditResult(NyaToolExecStatus status) => status switch
    {
        NyaToolExecStatus.Success    => "success",
        NyaToolExecStatus.Failed     => "error",
        NyaToolExecStatus.NotEnabled => "skipped",
        _                            => "error"
    };

    /// <summary>取得工具的審計分類標籤（schema.Group），供 LogToolCall 記錄。</summary>
    private string ResolveToolType(string toolName)
        => _toolRegistry.TryGetPlugin(toolName, out _, out var schema) ? (schema.Group ?? "tool") : "unknown";

    // ── 背景任務 ──────────────────────────────────────────────────────────────

    private async Task RunBackgroundTasksAsync(string userId, string topicId)
    {
        // 大項七 Fix B + 大項#3：摘要依 (user, topic) 去重，避免同一話題並發跑多個摘要任務；
        // 不同話題可各自摘要。TryAdd 為原子操作；跳過不會永久遺失觸發，下一則訊息會重新檢查門檻。
        var dedupKey = $"{userId}|{topicId}";
        if (!_summarizing.TryAdd(dedupKey, 0))
        {
            _logger.LogDebug("[NyaChat/BG] Summarization already running for {User}/{Topic}; skipped.",
                userId[..Math.Min(8, userId.Length)], topicId);
            return;
        }

        using var cts = new CancellationTokenSource(
            TimeSpan.FromSeconds(Math.Max(60, _config.FactExtractionTimeoutSeconds + 30)));
        try
        {
            await _summarizer.TrySummarizeAsync(userId, topicId, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaChat/BG] Summarization error for {User}", userId);
        }
        finally
        {
            _summarizing.TryRemove(dedupKey, out _); // 無論成功/失敗/逾時都釋放，避免旗標永久卡住
        }
    }

    // ── 工具方法 ──────────────────────────────────────────────────────────────

    private static NyaChatResult ErrorResult(string message, string error) => new()
    {
        Replies = new List<string> { message },
        Error   = error
    };

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
