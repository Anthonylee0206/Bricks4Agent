using System.Text;
using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// LLM 驅動的事實抽取服務。
/// 從最近對話中識別使用者的身份、偏好、明確指令等事實，
/// 並透過 NyaMemoryService 持久化（取代 HighLevelUserProfile 硬編碼）。
/// 此服務在對話完成後以 fire-and-forget 方式非同步執行，不阻塞回覆。
/// </summary>
public class NyaFactExtractor
{
    private readonly INyaConfigStore<NyaChatConfig> _configStore;
    private NyaChatConfig _config => _configStore.Current; // 當前配置快照
    private readonly NyaLlmClient _llmClient;
    private readonly NyaMemoryService _memoryService;
    private readonly NyaAuditLogger _auditLogger;
    private readonly NyaPromptTemplateProvider _templates; // 事實抗取 Prompt 模板外部化提供者
    private readonly ILogger<NyaFactExtractor> _logger;

    // 用來傳給 LLM 的對話訊息格式化行數上限
    private const int MaxMessagesForExtraction = 10;

    public NyaFactExtractor(
        INyaConfigStore<NyaChatConfig> configStore,
        NyaLlmClient llmClient,
        NyaMemoryService memoryService,
        NyaAuditLogger auditLogger,
        NyaPromptTemplateProvider templates,
        ILogger<NyaFactExtractor> logger)
    {
        _configStore = configStore;
        _llmClient = llmClient;
        _memoryService = memoryService;
        _auditLogger = auditLogger;
        _templates = templates;
        _logger = logger;
    }

    /// <summary>
    /// 從最近對話中非同步抽取事實，並持久化到 nya_facts。
    /// 若 LLM 失敗或回傳空結果，靜默跳過（不拋例外）。
    /// </summary>
    public async Task ExtractFactsAsync(string userId, CancellationToken ct = default)
    {
        try
        {
            var recentMessages = _memoryService.GetRecentMessages(userId, MaxMessagesForExtraction);
            if (recentMessages.Count < 2)
                return; // 對話太少，不值得抽取

            var existingFacts = _memoryService.GetActiveFacts(userId);
            var prompt = BuildExtractionPrompt(_templates, recentMessages, existingFacts);

            var request = new NyaLlmRequest
            {
                // 模型與取樣參數由事實抗取設定檔決定（低溫 0.2 已內建）；facade 會對齊。
                Messages = new List<NyaLlmMessage>
                {
                    new() { Role = "user", Content = prompt }
                }
            };

            var raw = await _llmClient.ExtractAsync(request, ct);
            if (string.IsNullOrWhiteSpace(raw))
            {
                _auditLogger.LogError(userId, "fact_extract", "llm_empty_response");
                return;
            }

            // 調和路徑（取代舊「只新增」扁平 facts）：LLM 回 add/update/retire 操作清單，
            // 經 NyaFactReconciler 守衛過濾後套用。retire 走 soft-retire（保留 history、可回復）。
            var ops = NyaFactReconciler.Parse(raw, NyaFactReconciler.Defaults);
            if (ops.Count == 0)
            {
                _auditLogger.LogFactExtractionEmpty(userId, request.Model, raw.Length, "no_ops_in_response");
                return;
            }

            var applied = ApplyReconcile(mem: _memoryService, userId: userId, ops: ops, logger: _logger);

            // 稽核每筆操作（帶 reason，可觀測/可追）。
            foreach (var op in ops)
                _auditLogger.LogToolResult(userId, $"fact_reconcile_{op.Op}", $"{op.Category}/{op.Key}: {op.Reason}");

            _logger.LogInformation("[NyaFactExtractor] reconcile applied {Applied}/{Total} ops for user {User}",
                applied, ops.Count, userId[..Math.Min(8, userId.Length)]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaFactExtractor] Extraction failed for user {User}", userId);
            _auditLogger.LogError(userId, "fact_extract", ex.Message);
        }
    }

    // ── Prompt ─────────────────────────────────────────────────────────────

    // 事實抗取提示詞固定指令文字取自外部模板（可熱重載）；動態資料仍由程式拼接。
    private static string BuildExtractionPrompt(
        NyaPromptTemplateProvider tpl,
        List<NyaMessage> recentMessages,
        List<NyaFact> existingFacts)
    {
        const string T = "fact_extraction";
        var sb = new StringBuilder();
        sb.AppendLine("<fact_extraction_instruction>");
        sb.AppendLine(tpl.Section(T, "instruction"));
        sb.AppendLine();
        sb.AppendLine(tpl.Section(T, "categories"));

        if (existingFacts.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<existing_facts>");
            sb.AppendLine(tpl.Section(T, "existing_facts_intro"));
            foreach (var f in existingFacts)
                sb.AppendLine($"  {f.Category}/{f.FactKey}: {f.FactValue}");
            sb.AppendLine("</existing_facts>");
        }

        sb.AppendLine();
        sb.AppendLine("<recent_conversation>");
        foreach (var m in recentMessages)
        {
            var roleLabel = string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase) ? "使用者" : "助理";
            sb.AppendLine($"[{roleLabel}] {m.Content}");
        }
        sb.AppendLine("</recent_conversation>");

        sb.AppendLine();
        sb.AppendLine("<output_format>");
        sb.AppendLine(tpl.Section(T, "output_format"));
        sb.AppendLine("</output_format>");
        sb.AppendLine("</fact_extraction_instruction>");

        return sb.ToString();
    }

    // ── 套用調和 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 套用調和操作到記憶。add/update → UpsertFact；retire → RetireFact（soft，可回復）。
    /// 回傳實際套用筆數。單筆失敗隔離（記 log、不中斷）。稽核由呼叫端 / 此處統一寫。
    /// <para>public 而非 internal：測試專案（Broker.Tests）無 InternalsVisibleTo，需跨組件直接呼叫。</para>
    /// </summary>
    public static int ApplyReconcile(
        NyaMemoryService mem, string userId, IReadOnlyList<NyaFactOp> ops, ILogger<NyaFactExtractor> logger)
    {
        var applied = 0;
        foreach (var op in ops)
        {
            try
            {
                switch (op.Op)
                {
                    case "add":
                    case "update":
                        mem.UpsertFact(userId, op.Category, op.Key, op.Value, null, (float)op.Confidence);
                        applied++;
                        break;
                    case "retire":
                        if (mem.RetireFact(userId, op.Key, op.Reason)) applied++;
                        break;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[NyaFactExtractor] reconcile op failed: {Op} {Key}", op.Op, op.Key);
            }
        }
        return applied;
    }

    // ── 解析 ────────────────────────────────────────────────────────────────

    /// <summary>
    /// ⚠️ 死碼（Dead code）— 已被 <see cref="NyaFactReconciler.Parse"/> 取代，不再被任何路徑呼叫。
    /// 原本用於解析舊式扁平 JSON 陣列回傳；調和流程與 NyaFactOp 擴充後已廢棄此路徑。
    /// 清理舊 API 時請一併移除本方法及 <c>FactExtractionResult</c> 化機型。
    /// </summary>
    [Obsolete("Dead code: replaced by NyaFactReconciler.Parse. Do not call.")]
    private List<FactExtractionResult> ParseFactsFromLlmResponse(string raw)
    {
        try
        {
            // 嘗試從 LLM 回覆中找到 JSON array（允許前後有文字）
            var start = raw.IndexOf('[');
            var end = raw.LastIndexOf(']');
            if (start < 0 || end < 0 || end <= start)
                return new List<FactExtractionResult>();

            var json = raw[start..(end + 1)];
            return JsonSerializer.Deserialize<List<FactExtractionResult>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new List<FactExtractionResult>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaFactExtractor] Failed to parse facts JSON from LLM response");
            return new List<FactExtractionResult>();
        }
    }
}
