namespace Broker.NyaChat.Abstractions;

/// <summary>
/// 工具執行上下文：保持精簡，只攜帶 Nya 核心已知的識別資訊與 LLM 提供的參數。
/// </summary>
/// <remarks>
/// 設計原則：插件自身的相依（HttpClient、broker mediator、DB…）由
/// 插件自己的建構式 DI 取得，**不**塞進此 context — Nya 核心不需要知道插件要什麼。
/// </remarks>
public sealed class NyaToolContext
{
    /// <summary>正在被呼叫的工具名稱（對應 <see cref="NyaToolSchema.Name"/>）。一個插件可暴露多個工具，故需指明。</summary>
    public string ToolName { get; init; } = "";

    /// <summary>正規化後的使用者識別（跨通道唯一）。</summary>
    public string UserId { get; init; } = "";

    /// <summary>來源通道（"line" / "discord" / "web"…）。</summary>
    public string ChannelType { get; init; } = "";

    /// <summary>對話 / 話題識別（可選）。</summary>
    public string? ConversationId { get; init; }

    /// <summary>Phase 4-A：本對話輪的全鏈路關聯 ID。插件發起 broker 側動作（TradingApprovalRequest）時沿用，使審批可反查原始對話。</summary>
    public string TraceId { get; init; } = "";

    /// <summary>LLM tool_call 提供的原始參數 JSON（由插件自行反序列化為自身參數型別）。</summary>
    public string ArgumentsJson { get; init; } = "{}";
}
