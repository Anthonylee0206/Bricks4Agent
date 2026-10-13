namespace Broker.NyaChat.Abstractions;

/// <summary>
/// 通道無關的對話請求。由各通道 adapter（LINE / Discord / Web）做完通道特有前置處理後正規化送入。
/// </summary>
/// <remarks>
/// 接受 <see cref="ChannelType"/> + <see cref="ChannelUserId"/> 而非單一 userId，
/// 由 Nya 內部結合兩者正規化，避免不同通道的同名 id 共用記憶。
/// <see cref="HighLevelCoordinator"/> 已全面改為透過 <see cref="INyaChatOrchestrator.ChatAsync"/> 傳入此請求，舊直接呼叫入口已不再使用。
/// </remarks>
public sealed class NyaChatRequest
{
    /// <summary>來源通道（"line" / "discord" / "web"…）。</summary>
    public string ChannelType { get; init; } = "";

    /// <summary>通道內的原始使用者 ID；由 Nya 內部結合 <see cref="ChannelType"/> 正規化。</summary>
    public string ChannelUserId { get; init; } = "";

    /// <summary>使用者訊息內容。</summary>
    public string Message { get; init; } = "";

    /// <summary>對話 / 話題識別（可選；null = 使用該使用者的當前 active 話題）。</summary>
    public string? ConversationId { get; init; }
}
