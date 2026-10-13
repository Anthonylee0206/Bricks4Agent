namespace Broker.NyaChat.Abstractions;

/// <summary>
/// 進 LLM 前的「字面指令」攔截擴充點。
/// </summary>
/// <remarks>
/// 目前僅提供<b>可用接口</b>：Orchestrator 在解析話題後、寫入使用者訊息前呼叫此 handler；
/// 若回傳 <see cref="NyaPreChatCommandResult.Handled"/> = true，則短路該訊息（不進 LLM、不寫入歷史），
/// 直接回傳指令結果。預設綁定無作用實作（<c>NullNyaPreChatCommandHandler</c>）；
/// 實際的 <c>/topic …</c> 等指令解析待後續實作以本接口接入，無須改 Orchestrator。
/// </remarks>
public interface INyaPreChatCommandHandler
{
    NyaPreChatCommandResult Handle(NyaPreChatCommandContext context);
}

/// <summary>字面指令攔截上下文。</summary>
public sealed class NyaPreChatCommandContext
{
    public string UserId { get; init; } = "";
    public string Message { get; init; } = "";
    public string ChannelType { get; init; } = "";
    /// <summary>本輪的當前話題 ID。</summary>
    public string TopicId { get; init; } = "";
}

/// <summary>字面指令攔截結果。</summary>
public sealed class NyaPreChatCommandResult
{
    /// <summary>true = 已作為指令處理並短路；false = 非指令，照常進 LLM。</summary>
    public bool Handled { get; init; }

    /// <summary>短路時要回給使用者的內容（可多段）。</summary>
    public IReadOnlyList<string> Replies { get; init; } = Array.Empty<string>();

    public static readonly NyaPreChatCommandResult NotHandled = new() { Handled = false };

    public static NyaPreChatCommandResult Reply(params string[] replies)
        => new() { Handled = true, Replies = replies };
}
