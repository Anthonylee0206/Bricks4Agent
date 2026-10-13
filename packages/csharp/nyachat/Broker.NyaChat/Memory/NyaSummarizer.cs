using System.Text;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 對話摘要壓縮服務。
/// 當使用者訊息總數（含已摘要）超過 SummarizeTriggerCount，
/// 將最舊的對話段落壓縮為長期摘要，並標記原始訊息為 is_summarized=true。
/// 原始訊息永不刪除，但不再被送入 LLM context。
/// 此服務在對話完成後以 fire-and-forget 方式非同步執行，不阻塞回覆。
/// </summary>
public class NyaSummarizer
{
    private readonly INyaConfigStore<NyaChatConfig> _configStore;
    private NyaChatConfig _config => _configStore.Current; // 當前配置快照
    private readonly NyaLlmClient _llmClient;
    private readonly NyaMemoryService _memoryService;
    private readonly NyaAuditLogger _auditLogger;
    private readonly NyaPromptTemplateProvider _templates; // 摘要 Prompt 模板外部化提供者
    private readonly ILogger<NyaSummarizer> _logger;

    public NyaSummarizer(
        INyaConfigStore<NyaChatConfig> configStore,
        NyaLlmClient llmClient,
        NyaMemoryService memoryService,
        NyaAuditLogger auditLogger,
        NyaPromptTemplateProvider templates,
        ILogger<NyaSummarizer> logger)
    {
        _configStore = configStore;
        _llmClient = llmClient;
        _memoryService = memoryService;
        _auditLogger = auditLogger;
        _templates = templates;
        _logger = logger;
    }

    /// <summary>
    /// 檢查是否需要摘要壓縮，若需要則執行。
    /// 靜默失敗，不影響主要對話流程。
    /// </summary>
    /// <param name="force">
    /// true = 手動「立即摘要」：略過自動觸發門檻（<see cref="NyaChatConfig.SummarizeTriggerCount"/>）。
    /// 修復：將對話摘要改為依話題獨立後，手動觸發只看 active 話題訊息數，切換/新開話題後常不足門檻 → 按鈕無作用。
    /// </param>
    public async Task<NyaSummarizeOutcome> TrySummarizeAsync(string userId, string topicId, CancellationToken ct = default, bool force = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(topicId))
                return new NyaSummarizeOutcome(NyaSummarizeStatus.NotEnoughMessages);

            // 摘要依話題獨立：僅獲取該指定話題的歷史訊息。
            var topicMessages = _memoryService.GetMessagesByTopic(userId, topicId);
            var toSummarize = PlanSummaryBatch(
                topicMessages, _config.SummarizeTriggerCount, _config.SummarizeKeepRecentCount, force,
                _config.SummarizeMaxBatchSize);
            if (toSummarize == null)
                return new NyaSummarizeOutcome(NyaSummarizeStatus.NotEnoughMessages);

            var fromSeq = toSummarize.First().Sequence;
            var toSeq = toSummarize.Last().Sequence;

            // 取得此話題現有 active 摘要，讓 V2+ 能繼承 V1 的內容
            var existingSummaries = _memoryService.GetActiveSummaries(userId, topicId);

            // 呼叫 LLM 生成摘要
            var summaryText = await GenerateSummaryAsync(userId, toSummarize, existingSummaries, ct);
            if (string.IsNullOrWhiteSpace(summaryText))
            {
                // Root cause A：thinking 模型把輸出預算耗在 <think> → content 回空 → 過去僅 LogWarning 後靜默 return，
                // 導致摘要頁/稽核頁皆無紀錄。改為一律寫稽核（result=skipped），失敗可觀測。
                _logger.LogWarning(
                    "[NyaSummarizer] LLM returned empty summary for user {User} topic {Topic} " +
                    "(thinking model may have exhausted the {Cap}-token output budget; consider raising SummarizeMaxOutputTokens or disabling thinking).",
                    userId[..Math.Min(8, userId.Length)], topicId, _config.SummarizeMaxOutputTokens);
                _auditLogger.LogSummarySkipped(userId, topicId, NyaLlmReasons.LlmEmpty);
                return new NyaSummarizeOutcome(NyaSummarizeStatus.LlmEmpty, Reason: NyaLlmReasons.LlmEmpty);
            }

            // 持久化摘要（綁定話題）
            var summary = _memoryService.CreateSummary(
                userId, topicId, summaryText, fromSeq, toSeq, toSummarize.Count);

            // 標記原始訊息為已摘要（限該話題，不再送入 LLM，但保留於 DB）
            _memoryService.MarkAsSummarized(userId, topicId, fromSeq, toSeq);

            _auditLogger.LogSummaryCreation(
                userId, topicId, summary.SummaryId, fromSeq, toSeq, toSummarize.Count,
                string.IsNullOrWhiteSpace(_config.SummarizationModel)
                    ? _config.ChatModel : _config.SummarizationModel);

            _logger.LogInformation(
                "[NyaSummarizer] Summarized {Count} messages (seq {From}-{To}) for user {User} topic {Topic}",
                toSummarize.Count, fromSeq, toSeq, userId[..Math.Min(8, userId.Length)], topicId);

            return new NyaSummarizeOutcome(NyaSummarizeStatus.Created, summary.SummaryId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaSummarizer] Summarization failed for user {User}", userId);
            _auditLogger.LogError(userId, "summary_create", ex.Message);
            return new NyaSummarizeOutcome(NyaSummarizeStatus.Error, Reason: ex.Message);
        }
    }

    /// <summary>
    /// 計算此話題要壓縮的訊息批次（純函式，便於測試）。回傳 null = 無可壓縮、不摘要。
    /// <para>
    /// <paramref name="force"/> = false（自動路徑）：<b>未摘要（active）</b>訊息數須達 <paramref name="triggerCount"/> 才壓縮。<br/>
    /// <paramref name="force"/> = true（手動「立即摘要」）：略過該門檻，只要有早於保留區的可壓縮訊息即可。
    /// </para>
    /// 兩者皆保留最新 <paramref name="keepRecent"/> 則、批次至少 2 則（短對話無需摘要 → null）。
    /// <para>
    /// <b>Root cause B（摺疊遲滯）</b>：門檻改看 active 數而非<b>總數</b>。舊版用總數（含已摘要）→
    /// 話題一旦越過門檻便永遠開著，每輪 +2 則就重觸發、形同沒摺疊。改看 active 後，摺疊（→ keepRecent）
    /// 會把 active 拉回門檻以下，須再累積 (triggerCount − keepRecent) 則新訊息才會下一次摺疊。
    /// </para>
    /// </summary>
    public static List<NyaMessage>? PlanSummaryBatch(
        IReadOnlyList<NyaMessage> topicMessages, int triggerCount, int keepRecent, bool force, int maxBatch = 0)
    {
        var active = topicMessages
            .Where(m => !m.IsSummarized)
            .OrderBy(m => m.Sequence)
            .ToList();

        if (!force && active.Count < triggerCount)
            return null; // 自動路徑：未摘要訊息尚未累積到門檻（摺疊後的遲滯區間）

        if (active.Count <= keepRecent)
            return null; // 保留最近 keepRecent 則後沒有更舊的可壓縮訊息

        var toSummarize = active.Take(active.Count - keepRecent).ToList();
        if (maxBatch > 0 && toSummarize.Count > maxBatch)
            toSummarize = toSummarize.Take(maxBatch).ToList(); // 小批次：每次只壓最舊的 maxBatch 則
        return toSummarize.Count < 2 ? null : toSummarize;
    }

    // ── Prompt ─────────────────────────────────────────────────────────────

    private async Task<string?> GenerateSummaryAsync(
        string userId,
        List<NyaMessage> messages,
        List<NyaSummary> existingSummaries,
        CancellationToken ct)
    {
        // 摘要提示詞固定指令文字取自外部模板（可熱重載）；動態資料仍由程式拼接。
        const string T = "summarization";
        var sb = new StringBuilder();
        sb.AppendLine("<summarization_instruction>");

        sb.AppendLine(existingSummaries.Count > 0
            ? _templates.Section(T, "intro_merge")
            : _templates.Section(T, "intro_fresh"));

        sb.AppendLine(_templates.Section(T, "requirements"));

        if (existingSummaries.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<prior_summary>");
            foreach (var s in existingSummaries.OrderBy(s => s.CoveredFromSeq))
                sb.AppendLine(s.SummaryText);
            sb.AppendLine("</prior_summary>");
        }

        sb.AppendLine();
        sb.AppendLine("<new_conversation>");

        foreach (var m in messages)
        {
            var roleLabel = string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                ? "使用者" : "助理";
            var timeHint = m.CreatedAt.ToString("MM/dd HH:mm");
            sb.AppendLine($"[{timeHint}][{roleLabel}] {m.Content}");
        }

        sb.AppendLine("</new_conversation>");
        sb.AppendLine();
        sb.AppendLine(_templates.Section(T, "closing"));
        sb.AppendLine("</summarization_instruction>");

        var request = new NyaLlmRequest
        {
            // 模型與取樣參數由摘要設定檔決定（低溫 0.3 已內建）；facade 會對齊。
            Messages = new List<NyaLlmMessage>
            {
                new() { Role = "user", Content = sb.ToString() }
            },
            MaxTokens = _config.SummarizeMaxOutputTokens // per-request 輸出上限（可設定，取代舊硬編碼 400）
        };

        return await _llmClient.SummarizeAsync(request, ct);
    }
}
