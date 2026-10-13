namespace Broker.NyaChat.Abstractions;

/// <summary>
/// 自描述工具插件契約（單一事實來源）：一個插件同時宣告 schema（給 LLM）與執行邏輯。
/// </summary>
/// <remarks>
/// 「最大程度歡迎其他插件」的編譯器層級保證：外部插件作者只需引用本契約專案
/// （<c>Broker.NyaChat.Abstractions</c>）、實作此介面，並以 <c>AddNyaTool&lt;T&gt;()</c> 一行註冊，
/// 不會也無法碰到 Nya 內部實作或 broker 內部型別。
/// 一個插件可暴露多個工具（例如量化插件同時有 get_price / place_order / portfolio）。
/// </remarks>
public interface INyaToolPlugin
{
    /// <summary>本插件暴露的工具 schema 集合（供 NyaToolRegistry 建構 function-calling 定義）。</summary>
    IReadOnlyList<NyaToolSchema> GetTools();

    /// <summary>
    /// 收到 tool_call 時由 Nya 呼叫；插件依 <see cref="NyaToolContext.ToolName"/> 自行分派並完成業務。
    /// </summary>
    Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken cancellationToken);
}
