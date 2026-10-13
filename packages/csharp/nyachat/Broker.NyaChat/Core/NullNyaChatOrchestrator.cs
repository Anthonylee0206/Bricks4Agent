using Abstractions = Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// <see cref="Abstractions.INyaChatOrchestrator"/> 的 no-op 替身。
/// 當 NyaChat 停用（<c>NyaChatConfig.Enabled=false</c>）或 DI 缺件時注入，
/// 使 broker 仍能啟動、消費端無需 null 判斷即可走既有 fallback。
/// </summary>
/// <remarks>
/// 注意：本檔命名空間為 <c>Broker.NyaChat</c>，與契約專案皆定義 <c>NyaChatResult</c>，
/// 同命名空間型別優先，故一律以 <c>Abstractions.</c> 前綴明確指向契約型別。
/// </remarks>
public sealed class NullNyaChatOrchestrator : Abstractions.INyaChatOrchestrator
{
    public bool IsEnabled => false;

    public Task<Abstractions.NyaChatResult> ChatAsync(Abstractions.NyaChatRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new Abstractions.NyaChatResult { Replies = Array.Empty<string>(), Error = null });
}
