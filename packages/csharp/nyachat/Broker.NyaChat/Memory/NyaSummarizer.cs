using System.Text;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

public class NyaSummarizer
{
    private readonly INyaConfigStore<NyaChatConfig> _configStore;
    private NyaChatConfig _config => _configStore.Current;
    private readonly NyaLlmClient _llmClient;
    private readonly NyaMemoryService _memoryService;
    private readonly NyaAuditLogger _auditLogger;
    private readonly NyaPromptTemplateProvider _templates;
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

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _background = new();

    public Task BackgroundCompletion(string userId) => _background.TryGetValue(userId, out var task) ? task : Task.CompletedTask;

    public void Queue(string userId, string topicId, string scope, string? conversationId)
    {
        _background.AddOrUpdate(userId, _ => RunBackground(userId, topicId, scope, conversationId),
            (_, previous) => previous.ContinueWith(_ => RunBackground(userId, topicId, scope, conversationId), TaskScheduler.Default).Unwrap());
    }

    private async Task RunBackground(string userId, string topicId, string scope, string? conversationId)
    {
        try
        {
            using var lease = await _memoryService.LockUserAsync(userId);
            await TrySummarizeAsync(userId, topicId, scope: scope, externalConversationId: conversationId);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[NyaSummarizer] Background summary failed for {User}", userId); }
    }

    public async Task<NyaSummarizeOutcome> TrySummarizeAsync(string userId, string topicId, CancellationToken ct = default, bool force = false, string scope = "normal", string? externalConversationId = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(topicId))
                return new NyaSummarizeOutcome(NyaSummarizeStatus.NotEnoughMessages);

            var visibilityVersion = _memoryService.VisibilityVersion(userId);
            var topicMessages = _memoryService.GetModelVisibleMessagesByTopic(userId, topicId, scope, externalConversationId);
            var toSummarize = PlanSummaryBatch(
                topicMessages, _config.SummarizeTriggerCount, _config.SummarizeKeepRecentCount, force,
                _config.SummarizeMaxBatchSize);
            if (toSummarize == null)
                return new NyaSummarizeOutcome(NyaSummarizeStatus.NotEnoughMessages);

            var fromSeq = toSummarize.First().Sequence;
            var toSeq = toSummarize.Last().Sequence;

            var existingSummaries = _memoryService.GetActiveSummaries(userId, topicId).Where(s => s.Scope == scope && (scope == "normal" || s.ConversationId == externalConversationId)).ToList();

            var summaryText = await GenerateSummaryAsync(userId, toSummarize, existingSummaries, ct);
            if (string.IsNullOrWhiteSpace(summaryText))
            {

                _logger.LogWarning(
                    "[NyaSummarizer] LLM returned empty summary for user {User} topic {Topic} " +
                    "(thinking model may have exhausted the {Cap}-token output budget; consider raising SummarizeMaxOutputTokens or disabling thinking).",
                    userId[..Math.Min(8, userId.Length)], topicId, _config.SummarizeMaxOutputTokens);
                _auditLogger.LogSummarySkipped(userId, topicId, NyaLlmReasons.LlmEmpty);
                return new NyaSummarizeOutcome(NyaSummarizeStatus.LlmEmpty, Reason: NyaLlmReasons.LlmEmpty);
            }

            var summary = _memoryService.CreateSummary(
                userId, topicId, summaryText, fromSeq, toSeq, toSummarize.Count, visibilityVersion, existingSummaries.Select(s => s.SummaryId).ToArray(), scope, externalConversationId);

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

    public static List<NyaMessage>? PlanSummaryBatch(
        IReadOnlyList<NyaMessage> topicMessages, int triggerCount, int keepRecent, bool force, int maxBatch = 0)
    {
        var active = topicMessages
            .Where(m => !m.IsSummarized)
            .OrderBy(m => m.Sequence)
            .ToList();

        if (!force && active.Count < triggerCount)
            return null;

        if (active.Count <= keepRecent)
            return null;

        var toSummarize = active.Take(active.Count - keepRecent).ToList();
        if (maxBatch > 0 && toSummarize.Count > maxBatch)
            toSummarize = toSummarize.Take(maxBatch).ToList();
        return toSummarize.Count < 2 ? null : toSummarize;
    }

    private async Task<string?> GenerateSummaryAsync(
        string userId,
        List<NyaMessage> messages,
        List<NyaSummary> existingSummaries,
        CancellationToken ct)
    {

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

            Messages = new List<NyaLlmMessage>
            {
                new() { Role = "user", Content = sb.ToString() }
            },
            MaxTokens = _config.SummarizeMaxOutputTokens
        };

        return await _llmClient.SummarizeAsync(request, ct);
    }
}
