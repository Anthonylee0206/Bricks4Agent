using System.Text;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

public partial class NyaPromptBuilder
{
    private const string Tpl = "system";

    private readonly INyaConfigStore<NyaChatConfig> _configStore;
    private readonly NyaToolRegistry _toolRegistry;
    private readonly NyaPromptTemplateProvider _templates;

    public NyaPromptBuilder(
        INyaConfigStore<NyaChatConfig> configStore,
        NyaToolRegistry toolRegistry,
        NyaPromptTemplateProvider templates)
    {
        _configStore = configStore;
        _toolRegistry = toolRegistry;
        _templates = templates;
    }

    public List<NyaLlmMessage> Build(
        string currentUserMessage,
        SoulDefinition soul,
        List<NyaNote> activeNotes,
        List<NyaSummary> activeSummaries,
        List<NyaMessage> recentMessages,
        string userId,
        string channelType)
        => Build(currentUserMessage, soul, activeNotes, activeSummaries, recentMessages,
                 NyaPromptBudget.Unlimited, NyaTokenEstimator.Default, userId, channelType, out _);

    public List<NyaLlmMessage> Build(
        string currentUserMessage,
        SoulDefinition soul,
        List<NyaNote> activeNotes,
        List<NyaSummary> activeSummaries,
        List<NyaMessage> recentMessages,
        NyaPromptBudget budget,
        NyaTokenEstimator estimator,
        string userId,
        string channelType,
        out NyaPromptBudgetReport report)
        => Build(currentUserMessage, soul, activeNotes, activeSummaries, recentMessages, budget, estimator,
                 userId, channelType, Array.Empty<NyaTopicEntry>(), "", out report);

    public List<NyaLlmMessage> Build(
        string currentUserMessage,
        SoulDefinition soul,
        List<NyaNote> activeNotes,
        List<NyaSummary> activeSummaries,
        List<NyaMessage> recentMessages,
        NyaPromptBudget budget,
        NyaTokenEstimator estimator,
        string userId,
        string channelType,
        IReadOnlyList<NyaTopicEntry> availableTopics,
        string currentTopicId,
        out NyaPromptBudgetReport report)
    {
        report = new NyaPromptBudgetReport();

        var usableTools = _toolRegistry.GetUsableSchemas(userId, channelType);
        var unavailableTools = _toolRegistry.GetUnavailableSchemas(userId, channelType);

        List<NyaNote> selectedNotes;
        List<NyaSummary> selectedSummaries;
        List<NyaMessage> selectedHistory;

        if (budget.IsUnlimited)
        {
            selectedNotes     = activeNotes;
            selectedSummaries = activeSummaries;
            selectedHistory   = recentMessages;
            report.NotesIncluded     = activeNotes.Count;
            report.SummariesIncluded = activeSummaries.Count;
            report.HistoryIncluded   = recentMessages.Count;
        }
        else
        {

            var fixedText = BuildSystemPrompt(soul, new List<NyaNote>(), new List<NyaSummary>(), usableTools, unavailableTools, availableTopics, currentTopicId);
            var fixedCost = estimator.Estimate(fixedText) + estimator.Estimate(currentUserMessage) + 8;
            var remaining = Math.Max(0, budget.InputTokenBudget - fixedCost);

            selectedNotes     = SelectNotes(activeNotes, estimator, remaining, budget.NoteRatio, report);
            var factsCost     = SumNoteTokens(selectedNotes, estimator);

            var summaryBudget = (int)(remaining * budget.SummaryRatio);
            selectedSummaries = SelectSummaries(activeSummaries, estimator, summaryBudget, report);
            var summariesCost = selectedSummaries.Sum(s => estimator.Estimate(s.SummaryText) + 2);

            var historyBudget = Math.Max(0, remaining - factsCost - summariesCost);
            selectedHistory   = SelectHistory(recentMessages, estimator, historyBudget, report);
        }

        var messages = new List<NyaLlmMessage>
        {
            new() { Role = "system", Content = BuildSystemPrompt(soul, selectedNotes, selectedSummaries, usableTools, unavailableTools, availableTopics, currentTopicId) }
        };
        DateTime? previousAt = null;
        foreach (var msg in selectedHistory)
        {
            var at = DateTime.SpecifyKind(msg.CreatedAt, msg.CreatedAt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : msg.CreatedAt.Kind).ToUniversalTime();
            var gap = previousAt == null ? DateTime.UtcNow - at : at - previousAt.Value;
            var content = msg.Role == "user" && gap >= TimeSpan.FromHours(3)
                ? $"[{at.ToLocalTime():yyyy-MM-dd HH:mm}] {msg.Content}"
                : msg.Content;
            messages.Add(new NyaLlmMessage { Role = msg.Role, Content = content });
            previousAt = at;
        }
        messages.Add(new NyaLlmMessage { Role = "user", Content = currentUserMessage });

        report.Selection = new
        {
            topic_id = currentTopicId,
            soul = new { id = soul.SoulId, definition = soul },
            history = selectedHistory.Select(m => new { message_id = m.MessageId, sequence = m.Sequence, role = m.Role, content = m.Content }).ToArray(),
            notes = selectedNotes.Select(f => new { fact_id = f.NoteId, key = f.Key, version = f.Version, value = f.Content, source_text = f.SourceMessageIdsJson, expires_at = f.ExpiresAt }).ToArray(),
            summaries = selectedSummaries.Select(s => new { summary_id = s.SummaryId, version = s.Version, text = s.SummaryText }).ToArray(),
            usable_tools = usableTools.Select(t => t.Name).ToArray(),
            unavailable_tools = unavailableTools.Select(t => new { name = t.Schema.Name, reason = t.Reason }).ToArray()
        };
        report.EstimatedTokens = estimator.Estimate(messages);
        return messages;
    }

    private static int NoteTokens(NyaNote f, NyaTokenEstimator est)
        => est.Estimate(f.Key) + est.Estimate(f.Content) + est.Estimate(NoteDate(f)) + 4;

    private static int SumNoteTokens(IEnumerable<NyaNote> facts, NyaTokenEstimator est)
        => facts.Sum(f => NoteTokens(f, est));

    private static List<NyaNote> SelectNotes(
        List<NyaNote> all, NyaTokenEstimator est, int remaining, double factRatio, NyaPromptBudgetReport report)
    {
        report.NotesIncluded = all.Count;
        report.MandatoryOverflow = SumNoteTokens(all, est) > remaining;
        return all;
    }

    private static List<NyaSummary> SelectSummaries(
        List<NyaSummary> all, NyaTokenEstimator est, int summaryBudget, NyaPromptBudgetReport report)
    {
        var selected = new List<NyaSummary>();
        var used = 0;
        foreach (var s in all.OrderByDescending(s => s.CoveredFromSeq))
        {
            var cost = est.Estimate(s.SummaryText) + 2;
            if (used + cost > summaryBudget) { report.SummariesDropped++; continue; }
            selected.Add(s);
            used += cost;
        }
        report.SummariesIncluded = selected.Count;

        return selected;
    }

    private static List<NyaMessage> SelectHistory(
        List<NyaMessage> recent, NyaTokenEstimator est, int historyBudget, NyaPromptBudgetReport report)
    {
        const int coherenceFloor = 2;
        var kept = new List<NyaMessage>();
        var used = 0;

        for (var i = recent.Count - 1; i >= 0; i--)
        {
            var cost = est.Estimate(recent[i].Content) + 4;
            if (used + cost > historyBudget && kept.Count >= coherenceFloor)
            {
                report.HistoryDropped += i + 1;
                break;
            }
            kept.Add(recent[i]);
            used += cost;
        }
        kept.Reverse();
        report.HistoryIncluded = kept.Count;
        return kept;
    }

    private string BuildCapabilities(
        IReadOnlyList<NyaToolSchema> usableTools,
        IReadOnlyList<(NyaToolSchema Schema, string Reason)> unavailableTools)
    {
        var sb = new StringBuilder();

        var statements = usableTools
            .Select(t => t.CapabilityStatement)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();

        if (statements.Count > 0)
        {
            sb.AppendLine(_templates.Section(Tpl, "capabilities_can_header"));
            foreach (var s in statements)
                sb.AppendLine($"- {s}");
            sb.AppendLine();
        }

        var closed = unavailableTools
            .Select(u => (Text: string.IsNullOrWhiteSpace(u.Schema.CapabilityStatement) ? u.Schema.Name : u.Schema.CapabilityStatement!, u.Reason))
            .GroupBy(u => u.Text).Select(g => g.First())
            .ToList();
        if (closed.Count > 0)
        {
            sb.AppendLine(_templates.Section(Tpl, "capabilities_closed_header"));
            foreach (var c in closed)
                sb.AppendLine($"- {c.Text}（{(c.Reason == "disabled" ? "管理員已關閉" : "這位使用者沒有權限")}）");
            sb.AppendLine();
        }

        sb.AppendLine(_templates.Section(Tpl, "capabilities_limit_header"));
        foreach (var limit in _templates.List(Tpl, "global_limits"))
            sb.AppendLine($"- {limit}");

        sb.AppendLine();
        sb.AppendLine(_templates.Section(Tpl, "capabilities_status_note"));

        return sb.ToString().TrimEnd();
    }

    private string BuildTask(IReadOnlyList<NyaToolSchema> usableTools)
    {
        var sb = new StringBuilder();
        sb.AppendLine(_templates.Section(Tpl, "behavior_directive"));

        bool Usable(string name) => usableTools.Any(t => t.Name == name);
        sb.AppendLine();
        sb.AppendLine(_templates.Section(Tpl, "memory_recent"));
        if (Usable("search_history")) sb.AppendLine(_templates.Section(Tpl, "memory_search"));
        if (Usable("note_save")) sb.AppendLine(_templates.Section(Tpl, "memory_write"));
        if (Usable("forget")) sb.AppendLine(_templates.Section(Tpl, "memory_forget"));
        if (Usable("note_save") || Usable("forget")) sb.AppendLine(_templates.Section(Tpl, "memory_ack"));

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine(_templates.Section(Tpl, "task_closing"));

        return sb.ToString().TrimEnd();
    }

    public string ToolResultDirective()
        => _templates.Section(Tpl, "tool_result_personalize");

    private static string NoteDate(NyaNote fact)
    {
        var noted = DateTime.SpecifyKind(fact.CreatedAt, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd");
        return fact.ExpiresAt is { } exp
            ? $"記於 {noted}，到 {DateTime.SpecifyKind(exp, DateTimeKind.Utc).ToLocalTime():yyyy-MM-dd}"
            : $"記於 {noted}";
    }

    private string BuildSystemPrompt(
        SoulDefinition soul,
        List<NyaNote> activeNotes,
        List<NyaSummary> activeSummaries,
        IReadOnlyList<NyaToolSchema> usableTools,
        IReadOnlyList<(NyaToolSchema Schema, string Reason)> unavailableTools,
        IReadOnlyList<NyaTopicEntry> availableTopics,
        string currentTopicId)
    {
        var sb = new StringBuilder();

        sb.AppendLine("<system_identity>");
        sb.AppendLine(_templates.Section(Tpl, "identity_name", new Dictionary<string, string> { ["display_name"] = soul.DisplayName }));
        sb.AppendLine(_templates.Section(Tpl, "identity_tone", new Dictionary<string, string> { ["tone"] = soul.Personality.Tone }));
        sb.AppendLine(_templates.Section(Tpl, "identity_language", new Dictionary<string, string> { ["language"] = soul.Personality.Language }));
        if (!string.IsNullOrWhiteSpace(soul.Personality.GreetingStyle))
            sb.AppendLine(_templates.Section(Tpl, "identity_greeting", new Dictionary<string, string> { ["greeting_style"] = soul.Personality.GreetingStyle }));
        if (!string.IsNullOrWhiteSpace(soul.Personality.HumorLevel))
            sb.AppendLine(_templates.Section(Tpl, "identity_humor", new Dictionary<string, string> { ["humor_level"] = soul.Personality.HumorLevel }));
        if (!string.IsNullOrWhiteSpace(soul.Personality.Formality))
            sb.AppendLine(_templates.Section(Tpl, "identity_formality", new Dictionary<string, string> { ["formality"] = soul.Personality.Formality }));

        if (soul.Rules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(_templates.Section(Tpl, "behavior_rules_header"));
            foreach (var rule in soul.Rules)
                sb.AppendLine($"- {rule}");
        }

        if (soul.Forbidden.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(_templates.Section(Tpl, "forbidden_header"));
            foreach (var f in soul.Forbidden)
                sb.AppendLine($"- {f}");
        }
        sb.AppendLine("</system_identity>");
        sb.AppendLine();

        sb.AppendLine("<system_capabilities>");
        sb.AppendLine(BuildCapabilities(usableTools, unavailableTools));
        sb.AppendLine("</system_capabilities>");
        sb.AppendLine();

        if (activeNotes.Count > 0)
        {
            sb.AppendLine("<long_term_notes>");
            sb.AppendLine(_templates.Section(Tpl, "facts_intro"));

            var grouped = activeNotes.GroupBy(f => f.Scope);
            foreach (var group in grouped)
            {
                var label = group.Key switch
                {
                    "identity"    => "【身份】",
                    "preference"  => "【偏好】",
                    "context"     => "【當前任務】",
                    "instruction" => "【使用者指令】",
                    _             => $"【{group.Key}】"
                };
                sb.AppendLine(label);
                foreach (var fact in group)
                    sb.AppendLine($"  {fact.Key}: {fact.Content}（{NoteDate(fact)}）");
            }

            sb.AppendLine();
            sb.AppendLine(_templates.Section(Tpl, "facts_guidance"));
            if (usableTools.Any(t => t.Name is "note_save" or "forget"))
                sb.AppendLine(_templates.Section(Tpl, "facts_guidance_edit"));
            sb.AppendLine("</long_term_notes>");
            sb.AppendLine();
        }

        if (activeSummaries.Count > 0)
        {
            sb.AppendLine("<long_term_memory>");
            sb.AppendLine(_templates.Section(Tpl, "summaries_intro"));
            foreach (var summary in activeSummaries.OrderBy(s => s.CoveredFromSeq))
                sb.AppendLine(summary.SummaryText);
            sb.AppendLine("</long_term_memory>");
            sb.AppendLine();
        }

        var topicToolAvailable = usableTools.Any(t =>
            t.Name is "switch_topic" or "new_topic");
        if (topicToolAvailable && availableTopics.Count > 0)
        {
            sb.AppendLine("<available_topics>");
            sb.AppendLine(_templates.Section(Tpl, "topics_header"));
            foreach (var t in availableTopics)
                sb.AppendLine($"- {t.TopicId}：{t.Title}{(t.TopicId == currentTopicId ? "（目前）" : "")}");
            sb.AppendLine("</available_topics>");
            sb.AppendLine();
        }

        sb.AppendLine("<task>");
        sb.AppendLine(BuildTask(usableTools));
        sb.AppendLine("</task>");
        sb.AppendLine();

        return sb.ToString().TrimEnd();
    }
    public string BuildStableSystem(SoulDefinition soul)
    {
        var all = _toolRegistry.GetAllSchemas().OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
        var complete = BuildSystemPrompt(soul, [], [], all, [], [], "");
        var identity = complete[..complete.IndexOf("<system_capabilities>", StringComparison.Ordinal)] + "<task>\n" + BuildTask(all) + "\n</task>";
        var directory = all.Select(s => new { name = s.Name, description = s.Description, scope = s.Group == "topic" ? "normal" : "server_checked" });
        return identity + "\n<工具目錄>\n" + System.Text.Json.JsonSerializer.Serialize(directory,
            new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n</工具目錄>\n" + ToolResultDirective() +
            "\n更正用 note_save（note_id）；遺忘用 forget_find 選候選後 forget，只保證選定引用不再可見，不承諾語意全忘。回想用 search_history、read_history。工作結果以 outcome 與 effects 為準；needs_confirmation、noop、partial 不可冒充成功，partial 須說明限制。";
    }

}
