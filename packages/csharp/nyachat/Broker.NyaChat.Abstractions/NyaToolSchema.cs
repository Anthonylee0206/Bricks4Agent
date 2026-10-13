namespace Broker.NyaChat.Abstractions;

/// <summary>
/// 工具的自描述 schema（單一事實來源）。
/// 同時供：(1) LLM function-calling 的工具定義、(2) admin 介面分組、
/// (3) 動態能力邊界文字生成（參見 <see cref="CapabilityStatement"/>）。
/// 由 <see cref="INyaToolPlugin.GetTools"/> 回傳，統一今天分裂的
/// <c>NyaToolDef</c>（靜態 Id/Name/Description）與工具執行邏輯。
/// </summary>
/// <remarks>支援豐富化 schema，並能由工具自行生成能力邊界陈述句。</remarks>
public sealed class NyaToolSchema
{
    /// <summary>function-calling 名稱（全域唯一，例如 "search" / "place_order"）。</summary>
    public string Name { get; init; } = "";

    /// <summary>工具描述（供 LLM 判斷何時呼叫）。</summary>
    public string Description { get; init; } = "";

    /// <summary>參數定義（多參數、型別、enum、required）。</summary>
    public IReadOnlyList<NyaToolParam> Parameters { get; init; } = Array.Empty<NyaToolParam>();

    /// <summary>admin 分組標籤（"memory" / "transport" / "quant"…）。</summary>
    public string? Group { get; init; }

    /// <summary>預設是否啟用（合併 nya_tools.json / DB 的啟停狀態時的初始值）。</summary>
    public bool DefaultEnabled { get; init; }

    /// <summary>
    /// 所需權限鍵；交由 <see cref="INyaToolAuthorizer"/> 判定（null = 無特別限制，所有人可用）。
    /// 「只有管理者能用量化交易工具」即由此 + broker 治理層決定。
    /// </summary>
    public string? RequiredPermission { get; init; }

    /// <summary>
    /// 能力邊界陳述句（例如「我可以幫你查詢即時股價並下單」）。
    /// 用於由「目前已註冊且該用戶有權使用的工具」動態生成 system prompt 能力描述，
    /// </summary>
    public string CapabilityStatement { get; init; } = "";
}
