namespace Broker.NyaChat;

/// <summary>
/// 單一 LLM profile（設定檔）。
///
/// 每個 profile <b>自帶完整 transport</b>（<see cref="Provider"/>/<see cref="BaseUrl"/>/<see cref="ApiKey"/>/
/// <see cref="Model"/>/<see cref="TimeoutSeconds"/>）+ 取樣參數 + 模型特定覆寫，使任務分流不再借用對話的
/// <c>ChatBaseUrl</c>/<c>ChatApiKey</c>——<b>從根上消除設定耦合 bug</b>
/// （抽取/摘要切了 provider 卻打到 Chat 的 BaseUrl 而靜默失敗）。
/// </summary>
public sealed class NyaLlmProfile
{
    /// <summary>"ollama" | "openai_chat" | "openai_responses"，對應已註冊 <c>INyaLlmProvider.Key</c>。</summary>
    public string Provider { get; set; } = "ollama";

    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "";

    public float Temperature { get; set; } = 0.7f;
    public float TopP { get; set; } = 0.9f;
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// 用於 Token 預算管理時的模型 Context Window 定義；本欄位僅<b>宣告、不消費</b>。
    /// 不同模型 context window 不同（qwen 8b≈8K、qwen 35b≈32K、GPT-4o≈128K），由 token 預算逻輯據此截斷。
    /// </summary>
    public int? MaxContextTokens { get; set; }

    /// <summary>
    /// 此設定檔模型的取樣參數覆寫，<b>取代</b>原本硬編碼於 <c>NyaLlmClient</c> 的
    /// <c>if (model == "qwen3.6:35b-a3b-Q4")</c> hack。
    /// <para>
    /// 採<b>扁平 param → value</b>（如 <c>{ "min_p": 0, "presence_penalty": 1.5, "top_k": 20 }</c>），
    /// 因為一個 profile 本就鎖定單一 <see cref="Model"/>，不需再以模型名稱當 key。
    /// <b>關鍵原因</b>：模型名稱（如 <c>qwen3.6:35b-a3b-Q4</c>）含冒號 <c>:</c>，而冒號是
    /// <c>ConfigurationBinder</c>（appsettings 路徑）的階層分隔符——若拿模型名稱當 key 會無法繫結。
    /// 扁平化後 key 皆為參數名（無冒號），三條路徑（appsettings / DB 覆寫 / PUT /config）皆正確繫結。
    /// </para>
    /// <para>
    /// 值型別固定為 <see cref="double"/>（現行所有硬編碼覆寫皆為數值）。若未來需要字串/布林覆寫，
    /// 屬擴展點（未來可擴充字串/布林覆寫支援）。
    /// </para>
    /// 由各 provider 套用至<b>各自正確落點</b>：ollama → <c>options</c>、openai_chat → 頂層。
    /// </summary>
    public Dictionary<string, double>? ModelOverrides { get; set; }

    /// <summary>
    /// 推理（thinking）開關（Qwen3 / DashScope 等相容端點）。對應 Python SDK 的
    /// <c>extra_body={"enable_thinking": ...}</c>——以原始 HTTP 而言即 body 頂層的 <c>enable_thinking</c>。
    /// null = 不送（維持端點預設）。設 false 可關閉思考、加快回覆。
    /// </summary>
    public bool? EnableThinking { get; set; }

    /// <summary>
    /// 思考 token 預算（對應 <c>extra_body={"thinking_budget": ...}</c>，body 頂層 <c>thinking_budget</c>）。
    /// 數值越小回覆越快。null = 不送。
    /// </summary>
    public int? ThinkingBudget { get; set; }
}
