namespace Broker.NyaChat;

/// <summary>
/// LLM 結果的標準化 reason code（寄宿功課 Phase 4-C 債1）。
/// </summary>
/// <remarks>
/// 收斂前：空回應在三處各寫各的（<c>llm_empty</c> / <c>llm_empty_response</c> /
/// 字面 "LLM returned empty content"），稽核頁無法聚合「thinking 模型空回應」總量。
/// 收斂後一律引用此處常量，讓稽核可一鍵統計同一根因。
/// 參見 docs/nyaplan/NyaChat-Error-Catalog（A-1b/A-1f/A-3a 同根因聚類）。
/// </remarks>
public static class NyaLlmReasons
{
    /// <summary>LLM 回非 null 但內容全空白（最常見根因：thinking 模型把輸出預算耗在 &lt;think&gt;）。</summary>
    public const string LlmEmpty = "llm_empty";

    /// <summary>LLM provider 回 null（HTTP 非 2xx / choices 空 / 結構不符）。</summary>
    public const string LlmUnavailable = "llm_unavailable";
}
