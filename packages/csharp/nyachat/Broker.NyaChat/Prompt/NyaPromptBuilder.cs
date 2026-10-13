using System.Text;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 結構化 System Prompt 建構器。
/// XML 分區讓 LLM 清楚辨識各段落用途：
///   system_identity: Soul 人格定義
///   system_capabilities: 能力邊界（能力由「已啟用且該用戶有權使用的工具」動態生成）
///   long_term_facts: 從 nya_facts 讀取的使用者長期記憶
///   long_term_memory: 從 nya_summaries 讀取的歷史摘要
///   task: 任務層（Doc 2：三層之末、最明確；行為指引 + 時間 + 程序性收尾）
/// 工具透過 function calling 傳遞，不再寫在 system prompt 裡。
/// </summary>
/// <remarks>
/// 所有固定鷹架文字由 <see cref="NyaPromptTemplateProvider"/> 外部化（JSON + 熱重載），
/// 能力邊界改為「<see cref="NyaToolRegistry.GetUsableSchemas"/> 各工具的 <c>CapabilityStatement</c>」動態組成，
/// 移除硬編碼且會被插件推翻的否定清單（如「無法執行交易」）。
/// </remarks>
public class NyaPromptBuilder
{
    private const string Tpl = "system"; // 模板 id

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

    /// <summary>
    /// 建構完整的 LLM 請求訊息陣列（相容多載：<b>不套用 Token 預算</b>，經典預設路徑）。
    /// </summary>
    public List<NyaLlmMessage> Build(
        string currentUserMessage,
        SoulDefinition soul,
        List<NyaFact> activeFacts,
        List<NyaSummary> activeSummaries,
        List<NyaMessage> recentMessages,
        string userId,
        string channelType)
        => Build(currentUserMessage, soul, activeFacts, activeSummaries, recentMessages,
                 NyaPromptBudget.Unlimited, NyaTokenEstimator.Default, userId, channelType, out _);

    /// <summary>
    /// 建構完整的 LLM 請求訊息陣列（套用 Token 預算、動態能力邊界 + 模板 + 時間）。
    /// [0] = system message、[1..N-1] = 歷史對話、[N] = 當前使用者訊息。
    /// </summary>
    /// <remarks>
    /// 超出 <paramref name="budget"/> 時的取捨：固定段（identity/capabilities/instructions/當前訊息）+
    /// <b>必留事實（instruction/identity）永不砍</b>；剩餘預算依優先級填入
    /// optional facts（context&gt;preference，confidence、recency 為次序）→ summaries（新→舊）→
    /// history（新→舊，保留至少最近一輪）。犧牲順序與之相反（history 先被砍）。
    /// <para><paramref name="userId"/> / <paramref name="channelType"/> 供能力邊界依「該用戶可用的工具」動態生成。</para>
    /// </remarks>
    // 既有 9-arg 全版（保留簽章）→ 委派給含話題的新版（空話題）。
    public List<NyaLlmMessage> Build(
        string currentUserMessage,
        SoulDefinition soul,
        List<NyaFact> activeFacts,
        List<NyaSummary> activeSummaries,
        List<NyaMessage> recentMessages,
        NyaPromptBudget budget,
        NyaTokenEstimator estimator,
        string userId,
        string channelType,
        out NyaPromptBudgetReport report)
        => Build(currentUserMessage, soul, activeFacts, activeSummaries, recentMessages, budget, estimator,
                 userId, channelType, Array.Empty<NyaTopicEntry>(), "", out report);

    // 新版：含可切換話題清單（item 8）。
    public List<NyaLlmMessage> Build(
        string currentUserMessage,
        SoulDefinition soul,
        List<NyaFact> activeFacts,
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

        // 能力與指引依「該用戶 / 通道可用的工具」生成。一次解析、固定段與最終組裝共用，確保一致。
        var usableTools = _toolRegistry.GetUsableSchemas(userId, channelType);

        List<NyaFact> selectedFacts;
        List<NyaSummary> selectedSummaries;
        List<NyaMessage> selectedHistory;

        if (budget.IsUnlimited)
        {
            selectedFacts     = activeFacts;
            selectedSummaries = activeSummaries;
            selectedHistory   = recentMessages;
            report.FactsIncluded     = activeFacts.Count;
            report.SummariesIncluded = activeSummaries.Count;
            report.HistoryIncluded   = recentMessages.Count;
        }
        else
        {
            // 固定段成本：以「無 facts/summaries」的 system prompt 估上界（含 identity/capabilities/instructions 鷹架）。
            var fixedText = BuildSystemPrompt(soul, new List<NyaFact>(), new List<NyaSummary>(), usableTools, availableTopics, currentTopicId);
            var fixedCost = estimator.Estimate(fixedText) + estimator.Estimate(currentUserMessage) + 8;
            var remaining = Math.Max(0, budget.InputTokenBudget - fixedCost);

            selectedFacts     = SelectFacts(activeFacts, estimator, remaining, budget.FactRatio, report);
            var factsCost     = SumFactTokens(selectedFacts, estimator);

            var summaryBudget = (int)(remaining * budget.SummaryRatio);
            selectedSummaries = SelectSummaries(activeSummaries, estimator, summaryBudget, report);
            var summariesCost = selectedSummaries.Sum(s => estimator.Estimate(s.SummaryText) + 2);

            var historyBudget = Math.Max(0, remaining - factsCost - summariesCost);
            selectedHistory   = SelectHistory(recentMessages, estimator, historyBudget, report);
        }

        var messages = new List<NyaLlmMessage>
        {
            new() { Role = "system", Content = BuildSystemPrompt(soul, selectedFacts, selectedSummaries, usableTools, availableTopics, currentTopicId) }
        };
        foreach (var msg in selectedHistory)
            messages.Add(new NyaLlmMessage { Role = msg.Role, Content = msg.Content });
        messages.Add(new NyaLlmMessage { Role = "user", Content = currentUserMessage });

        report.EstimatedTokens = estimator.Estimate(messages);
        return messages;
    }

    // ── 預算選取邏輯 ──────────────────────────────────────────────────────────────────

    /// <summary>category 截斷優先級：instruction ≈ identity（必留）> context > preference。</summary>
    private static int CategoryRank(string category) => category switch
    {
        "instruction" => 0,
        "identity"    => 0,
        "context"     => 1,
        "preference"  => 2,
        _             => 3
    };

    private static bool IsMandatory(string category)
        => category is "instruction" or "identity";

    private static int FactTokens(NyaFact f, NyaTokenEstimator est)
        => est.Estimate(f.FactKey) + est.Estimate(f.FactValue) + 2;

    private static int SumFactTokens(IEnumerable<NyaFact> facts, NyaTokenEstimator est)
        => facts.Sum(f => FactTokens(f, est));

    /// <summary>必留事實永遠納入；optional 依優先級填入至 facts 上限。</summary>
    private static List<NyaFact> SelectFacts(
        List<NyaFact> all, NyaTokenEstimator est, int remaining, double factRatio, NyaPromptBudgetReport report)
    {
        var mandatory = all.Where(f => IsMandatory(f.Category)).ToList();
        var optional  = all.Where(f => !IsMandatory(f.Category))
                           .OrderBy(f => CategoryRank(f.Category))
                           .ThenByDescending(f => f.Confidence)
                           .ThenByDescending(f => f.CreatedAt)
                           .ToList();

        var selected = new List<NyaFact>(mandatory);
        var mandatoryCost = SumFactTokens(mandatory, est);
        if (mandatoryCost > remaining) report.MandatoryOverflow = true;

        var factBudget = (int)(remaining * factRatio);
        var used = mandatoryCost; // 必留先佔用總剩餘
        foreach (var f in optional)
        {
            var cost = FactTokens(f, est);
            if (used + cost > remaining || (SumFactTokens(selected, est) - mandatoryCost) + cost > factBudget)
            {
                report.FactsDropped++;
                continue;
            }
            selected.Add(f);
            used += cost;
        }

        report.FactsIncluded = selected.Count;
        return selected;
    }

    /// <summary>summaries 新（高 CoveredFromSeq）優先填入至上限。</summary>
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
        // BuildSystemPrompt 內會再依 CoveredFromSeq 升序排列，這裡只決定「選哪些」
        return selected;
    }

    /// <summary>history 新→舊填入至預算；保留至少最近一輪（最後 2 則）以維持連貫。</summary>
    private static List<NyaMessage> SelectHistory(
        List<NyaMessage> recent, NyaTokenEstimator est, int historyBudget, NyaPromptBudgetReport report)
    {
        const int coherenceFloor = 2;
        var kept = new List<NyaMessage>();
        var used = 0;
        // recent 為舊→新；由新到舊納入
        for (var i = recent.Count - 1; i >= 0; i--)
        {
            var cost = est.Estimate(recent[i].Content) + 4;
            if (used + cost > historyBudget && kept.Count >= coherenceFloor)
            {
                report.HistoryDropped++;
                continue;
            }
            kept.Add(recent[i]);
            used += cost;
        }
        kept.Reverse(); // 還原為舊→新
        report.HistoryIncluded = kept.Count;
        return kept;
    }

    // ── 動態能力邊界組裝 ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 動態能力邊界：可用能力由 <paramref name="usableTools"/> 的 <c>CapabilityStatement</c> 逐句生成；
    /// 限制只列模板的全域不變項。<b>不</b>重複 function-calling schema 的工具名稱/參數（5B）。
    /// </summary>
    private string BuildCapabilities(IReadOnlyList<NyaToolSchema> usableTools)
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

        sb.AppendLine(_templates.Section(Tpl, "capabilities_limit_header"));
        foreach (var limit in _templates.List(Tpl, "global_limits"))
            sb.AppendLine($"- {limit}");

        sb.AppendLine();
        sb.AppendLine(_templates.Section(Tpl, "capabilities_status_note"));

        return sb.ToString().TrimEnd();
    }

    // ── 行為指引與任務層組裝 ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 任務層（三層之末、最明確）：行為風格 + 記憶操作指引（記憶工具可用時）+ 當前時間 + 程序性收尾。
    /// 設計 Doc 2：任務層永遠在最後，明確告訴模型「這一輪要做什麼、輸出什麼格式」。
    /// </summary>
    private string BuildTask(IReadOnlyList<NyaToolSchema> usableTools)
    {
        var sb = new StringBuilder();
        sb.AppendLine(_templates.Section(Tpl, "behavior_directive"));

        var memoryAvailable = usableTools.Any(t =>
            string.Equals(t.Group, "memory", StringComparison.OrdinalIgnoreCase));
        if (memoryAvailable)
        {
            sb.AppendLine();
            sb.AppendLine(_templates.Section(Tpl, "memory_guidance"));
        }

        sb.AppendLine();
        sb.AppendLine(_templates.Section(Tpl, "instructions_time",
            new Dictionary<string, string> { ["current_datetime"] = NowText() }));

        sb.AppendLine();
        sb.AppendLine(_templates.Section(Tpl, "task_closing"));

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 閉環二次回覆（工具結果個性化）的任務層指令：用人格轉述工具結果、勿改數字/事實（Doc 2 surface #2 契約）。
    /// 由 <see cref="NyaChatOrchestrator"/> 在第二次 LLM 呼叫前以 system 訊息追加。
    /// </summary>
    public string ToolResultDirective()
        => _templates.Section(Tpl, "tool_result_personalize");

    /// <summary>當前日期時間（伺服器時區；時區可配置屬後續擴展點）。</summary>
    private static string NowText()
    {
        var now = DateTimeOffset.Now;
        string[] wd = { "週日", "週一", "週二", "週三", "週四", "週五", "週六" };
        return $"{now:yyyy-MM-dd HH:mm}（{wd[(int)now.DayOfWeek]}）";
    }

    // ── System Prompt ─────────────────────────────────────────────────────────

    private string BuildSystemPrompt(
        SoulDefinition soul,
        List<NyaFact> activeFacts,
        List<NyaSummary> activeSummaries,
        IReadOnlyList<NyaToolSchema> usableTools,
        IReadOnlyList<NyaTopicEntry> availableTopics,
        string currentTopicId)
    {
        var sb = new StringBuilder();

        // ── Soul 人格 ──────────────────────────────────────────────────────
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

        // ── 能力邊界（5B/5E：動態）─────────────────────────────────────────
        sb.AppendLine("<system_capabilities>");
        sb.AppendLine(BuildCapabilities(usableTools));
        sb.AppendLine("</system_capabilities>");
        sb.AppendLine();

        // ── 長期記憶（事實） ──────────────────────────────────────────────
        if (activeFacts.Count > 0)
        {
            sb.AppendLine("<long_term_facts>");
            sb.AppendLine(_templates.Section(Tpl, "facts_intro"));

            var grouped = activeFacts.GroupBy(f => f.Category);
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
                    sb.AppendLine($"  {fact.FactKey}: {fact.FactValue}");
            }

            sb.AppendLine();
            sb.AppendLine(_templates.Section(Tpl, "facts_guidance"));
            sb.AppendLine("</long_term_facts>");
            sb.AppendLine();
        }

        // ── 長期摘要 ──────────────────────────────────────────────────────
        if (activeSummaries.Count > 0)
        {
            sb.AppendLine("<long_term_memory>");
            sb.AppendLine(_templates.Section(Tpl, "summaries_intro"));
            foreach (var summary in activeSummaries.OrderBy(s => s.CoveredFromSeq))
                sb.AppendLine(summary.SummaryText);
            sb.AppendLine("</long_term_memory>");
            sb.AppendLine();
        }

        // ── 可切換話題（item 8）：僅當話題工具可用 + 有話題時注入 ──────────────
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

        // ── 任務層（Doc 2：三層之末、最明確；這一輪做什麼 + 程序性收尾）──────
        sb.AppendLine("<task>");
        sb.AppendLine(BuildTask(usableTools));
        sb.AppendLine("</task>");
        sb.AppendLine();

        return sb.ToString().TrimEnd();
    }
}
