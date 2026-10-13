namespace Broker.NyaChat;

/// <summary>
/// LLM 任務名稱常量。對應 <see cref="NyaChatConfig.TaskRouting"/> 的 key
/// 與每個任務的預設 profile 名稱。
/// </summary>
public static class NyaLlmTasks
{
    public const string Chat           = "chat";
    public const string FactExtraction = "fact_extraction";
    public const string Summarization  = "summarization";
}

/// <summary>
/// 單一 LLM provider 的策略契約。
/// 每個 provider（ollama / openai_chat / openai_responses / 未來 anthropic…）獨立實作，
/// 由 <see cref="NyaLlmClient"/> 依 profile 的 <c>Provider</c> 鍵選用。
/// </summary>
/// <remarks>
/// provider 屬 Nya 內部組裝物，<b>不對 broker 暴露介面</b>（僅
/// <c>INyaChatOrchestrator</c> 需對外）。新增 provider = 一個類別 + 一行
/// <c>AddNyaLlmProvider&lt;T&gt;()</c>，與 <c>AddNyaTool&lt;T&gt;()</c> 對稱。
/// </remarks>
public interface INyaLlmProvider
{
    /// <summary>provider 鍵，需與 <see cref="NyaLlmProfile.Provider"/> 比對（小寫）。</summary>
    string Key { get; }

    /// <summary>
    /// 此 provider 是否支援 function calling（送 tools / 序列化 role=tool）。
    /// 預設 <c>true</c>；不支援者（如 <c>openai_responses</c>）覆寫為 <c>false</c>，
    /// 供 facade 偵測「帶 tools 的 chat 任務被路由到不支援工具的 provider → 工具閉環靜默失效」並告警。
    /// </summary>
    bool SupportsTools => true;

    /// <summary>依 profile 的 transport 與 request 發送請求，回傳結構化回應（含內容或 tool_calls）。</summary>
    Task<NyaLlmResponse?> SendAsync(NyaLlmProviderRequest request, CancellationToken ct);
}

/// <summary>
/// 由 <see cref="NyaLlmClient"/> facade 組裝交給 provider 的封包：
/// 解析後的 <see cref="NyaLlmProfile"/>（transport + 覆寫）+ 既有 <see cref="NyaLlmRequest"/>。
/// </summary>
public sealed class NyaLlmProviderRequest
{
    /// <summary>已解析的 profile：BaseUrl / ApiKey / TimeoutSeconds / Model / ModelOverrides。</summary>
    public required NyaLlmProfile Profile { get; init; }

    /// <summary>請求內容：Messages / Temperature / TopP / MaxTokens / Tools（Model 已由 facade 對齊 profile）。</summary>
    public required NyaLlmRequest Request { get; init; }
}
