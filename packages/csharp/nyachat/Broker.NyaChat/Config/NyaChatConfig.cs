namespace Broker.NyaChat;

/// <summary>
/// NyaChatEngine V2 統一配置。
/// 對應 appsettings.json "NyaChat" 段落。
/// </summary>
public class NyaChatConfig
{
    // ── 開關 ──────────────────────────────────────────────────────────────────
    /// <summary>
    /// 是否啟用 V2 引擎。
    /// false = 繼續使用舊 LineChatGateway；true = 使用 NyaChatOrchestrator。
    /// </summary>
    public bool Enabled { get; set; } = true;

    // ── 資料隔離（2026-06-13 原則：broker 不過 AI 產出）──────────────────────
    /// <summary>
    /// NyaChat 專屬 SQLite 路徑。所有 AI 產出（訊息/事實/摘要/話題/稽核/配置覆寫）
    /// 寫入此庫，<b>不寫 broker.db</b>；broker.db 可隨時刪除重建而不影響 Nya 記憶。
    /// 治理資料（nya_principal_bindings）屬授權非 AI 產出，仍留 broker.db。
    /// </summary>
    public string DbPath { get; set; } = "nyachat.db";

    // ── 記憶設定 ──────────────────────────────────────────────────────────────
    /// <summary>送入 LLM 的短期記憶最大則數（is_summarized=false 且 is_deleted=false）</summary>
    public int ShortTermMessageLimit { get; set; } = 30;

    /// <summary>累積到多少則（含 is_summarized）後觸發摘要壓縮（小批次勤摘要 → 調小）</summary>
    public int SummarizeTriggerCount { get; set; } = 12;

    /// <summary>摘要壓縮時，保留最新幾則作為短期記憶（其餘標為 is_summarized）</summary>
    public int SummarizeKeepRecentCount { get; set; } = 6;

    /// <summary>單次摘要最多壓縮幾則訊息（小批次勤摘要；&lt;=0 表不設上限）。</summary>
    public int SummarizeMaxBatchSize { get; set; } = 8;

    /// <summary>
    /// 摘要請求的輸出 token 上限（取代舊硬編碼 400）。
    /// thinking 模型（如 Qwen3）會把輸出預算耗在 &lt;think&gt; 段落，過小（如 400）會導致
    /// content 回空字串 → 摘要靜默失敗。預設提高並可調整，確保 reasoning + 摘要都放得下。
    /// </summary>
    public int SummarizeMaxOutputTokens { get; set; } = 2048;

    /// <summary>每個事實 key 保留的版本數（超過時刪最舊）</summary>
    public int FactVersionRetention { get; set; } = 5;

    /// <summary>每個 user 的摘要保留版本數</summary>
    public int SummaryVersionRetention { get; set; } = 5;

    // ── LLM 設定檔（Profiles）與任務路由 ──────────────────────────────────────
    /// <summary>
    /// 具名 LLM profile 集合（key = profile 名稱）。每個 profile 自帶完整 transport + 取樣 + 模型覆寫。
    /// 為空時，<see cref="StaticTaskRouter"/> 由下方 legacy 扁平欄位自動合成（向後相容，零部署改動）。
    /// </summary>
    public Dictionary<string, NyaLlmProfile> LlmProfiles { get; set; } = new();

    /// <summary>
    /// 任務 → profile 名稱的靜態映射（key 見 <see cref="NyaLlmTasks"/>：chat / fact_extraction / summarization）。
    /// 缺鍵時回退到該任務的預設 profile 名稱。動態策略（用戶等級/複雜度/負載）屬擴展點，本項不實作（審查2）。
    /// </summary>
    public Dictionary<string, string> TaskRouting { get; set; } = new();

    // ── LLM 設定（對話）── [legacy]：保留作向後相容 fallback，當 LlmProfiles 為空時合成 chat profile ──
    /// <summary>"ollama" | "openai_chat" | "openai_responses"</summary>
    public string ChatProvider { get; set; } = "ollama";
    public string ChatBaseUrl { get; set; } = "http://localhost:11434";
    public string ChatApiKey { get; set; } = "";
    public string ChatModel { get; set; } = "qwen3.6:35b-a3b-Q4";
    public float Temperature { get; set; } = 0.7f;
    public float TopP { get; set; } = 0.9f;
    public int ChatTimeoutSeconds { get; set; } = 120;

    // ── LLM 設定（事實抽取）── [legacy] 空=使用 ChatModel；LlmProfiles 為空時合成 extraction profile ──
    public string FactExtractionProvider { get; set; } = "";
    public string FactExtractionModel { get; set; } = "";
    public int FactExtractionTimeoutSeconds { get; set; } = 60;

    // ── LLM 設定（摘要）── [legacy] 空=使用 ChatModel；LlmProfiles 為空時合成 summarization profile ──
    public string SummarizationProvider { get; set; } = "";
    public string SummarizationModel { get; set; } = "";
    public int SummarizationTimeoutSeconds { get; set; } = 90;

    // \u2500\u2500 Token \u9810\u7b97\u7ba1\u7406────────────────────────────────────────────────────
    /// <summary>chat profile 未設 <see cref="NyaLlmProfile.MaxContextTokens"/> 時的保守回退總額（審查3：偏小）。</summary>
    public int DefaultMaxContextTokens { get; set; } = 8192;

    /// <summary>保留給模型輸出的 context 比例（其餘為 input 預算）。</summary>
    public double OutputReserveRatio { get; set; } = 0.25;

    /// <summary>輸出保留額下限（token）。</summary>
    public int MinOutputTokens { get; set; } = 256;

    /// <summary>輸出保留額上限（token）。</summary>
    public int MaxOutputTokens { get; set; } = 2048;

    /// <summary>估算誤差緩衝（從總額額外扣除，吸收估算不準）。</summary>
    public int ContextSafetyMargin { get; set; } = 256;

    /// <summary>input 預算中（扣固定段與必留事實後）facts 的上限占比。</summary>
    public double FactTokenBudgetRatio { get; set; } = 0.40;

    /// <summary>input 預算中 summaries 的上限占比（其餘留給 history）。</summary>
    public double SummaryTokenBudgetRatio { get; set; } = 0.35;

    /// <summary>單一工具結果餵回第二次 LLM 前的 token 上限（3D，超過則截尾）。</summary>
    public int PerToolResultMaxTokens { get; set; } = 1024;

    /// <summary>工具閉環最大回合數：>1 時 LLM 可依前輪結果續查工具；最後一輪不帶 tools，以計數器防遞迴。</summary>
    public int MaxToolRounds { get; set; } = 3;

    /// <summary>單一工具執行逾時秒數（linked CTS）：慢插件逾時視為該工具失敗，不拖死整輪對話。</summary>
    public int ToolExecTimeoutSeconds { get; set; } = 30;

    /// <summary>token 估算：CJK 字元的 token/char 比率（啟動期固定）。</summary>
    public double TokenEstimatorCjkPerChar { get; set; } = 1.5;

    /// <summary>token 估算：非 CJK 字元的 token/char 比率（啟動期固定）。</summary>
    public double TokenEstimatorLatinPerChar { get; set; } = 0.25;

    // ── 回覆後處理 ───────────────────────────────────────────────────────────
    /// <summary>單段回覆的最大字元數（超過則切割）</summary>
    public int MaxReplyLength { get; set; } = 2000;

    /// <summary>是否將 Markdown 格式降級為 LINE 適配純文字</summary>
    public bool StripMarkdown { get; set; } = true;

    /// <summary>是否允許長回覆切割為多段傳送</summary>
    public bool EnableMultiPartReply { get; set; } = true;

    // ── RAG（預留，目前不啟用）──────────────────────────────────────────────
    public bool RagEnabled { get; set; } = false;

    // ── Soul ─────────────────────────────────────────────────────────────────
    public string DefaultSoulId { get; set; } = "default";

    /// <summary>Soul JSON 定義檔所在目錄（相對於執行路徑）</summary>
    public string SoulsDirectory { get; set; } = "souls";

    // \u2500\u2500 Prompt \u6a21\u677f───────────────────────────────────────────────
    /// <summary>Prompt 模板 JSON 檔所在目錄（相對於執行路徑）。啟動期固定（PUT /config 唯讀）。</summary>
    public string PromptsDirectory { get; set; } = "prompts";

    // ── 審計 ─────────────────────────────────────────────────────────────────
    public bool AuditEnabled { get; set; } = true;
}
