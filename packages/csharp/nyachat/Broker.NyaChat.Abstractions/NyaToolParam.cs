namespace Broker.NyaChat.Abstractions;

/// <summary>
/// 單一工具參數定義（function-calling schema 的 property）。
/// 支援型別、enum、必填標記，取代舊 <c>NyaToolRegistry.BuildExternalLlmTools</c> 只有單一
/// <c>query</c> 參數的限制（量化插件可能需要 symbol / amount / action 等多參數）。
/// </summary>
/// <remarks>支援多參數工具（型別、enum、required），可直接對映 function-calling schema properties。</remarks>
public sealed class NyaToolParam
{
    /// <summary>參數名稱（function-calling property key，例如 "symbol"）。</summary>
    public string Name { get; init; } = "";

    /// <summary>JSON Schema 型別："string" | "number" | "integer" | "boolean" | "array" | "object"。</summary>
    public string Type { get; init; } = "string";

    /// <summary>參數說明（供 LLM 理解用途）。</summary>
    public string Description { get; init; } = "";

    /// <summary>是否必填。</summary>
    public bool Required { get; init; }

    /// <summary>列舉值（可選）；非空時 LLM 僅能從中選擇。</summary>
    public IReadOnlyList<string>? Enum { get; init; }
}
