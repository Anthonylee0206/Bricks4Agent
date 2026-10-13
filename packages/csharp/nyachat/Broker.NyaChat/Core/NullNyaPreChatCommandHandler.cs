using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 字面指令攔截的預設無作用實作：一律回 <see cref="NyaPreChatCommandResult.NotHandled"/>，
/// 即所有訊息照常進 LLM。實際指令解析待後續實作覆蓋此綁定。
/// </summary>
public sealed class NullNyaPreChatCommandHandler : INyaPreChatCommandHandler
{
    public NyaPreChatCommandResult Handle(NyaPreChatCommandContext context)
        => NyaPreChatCommandResult.NotHandled;
}
