namespace Broker.NyaChat.Abstractions;

/// <summary>
/// NyaChat 對外唯一入口（通道無關）。broker 與其他通道 adapter 只依賴此介面。
/// </summary>
/// <remarks>
/// 實作需切換到此契約並自行完成「工具 → 結果 → LLM 二次回覆」閉環。
/// 提供 <c>NullNyaChatOrchestrator</c> 供消費端以可空注入消除硬依賴，
/// 使 NyaChat 停用或註冊失敗時 broker 仍能啟動。
/// 此介面已由 <c>NyaChatOrchestrator</c> 完整實作，並通過全部閉環回歸測試。
/// </remarks>
public interface INyaChatOrchestrator
{
    /// <summary>引擎是否啟用。停用時消費端應走既有 fallback。</summary>
    bool IsEnabled { get; }

    /// <summary>通道無關的對話入口：自行完成記憶、工具與回覆閉環。</summary>
    Task<NyaChatResult> ChatAsync(NyaChatRequest request, CancellationToken cancellationToken);
}
