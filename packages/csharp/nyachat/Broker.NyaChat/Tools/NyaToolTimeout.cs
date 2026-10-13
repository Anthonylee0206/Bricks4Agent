using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 單一工具執行的逾時包裝：以 linked CTS 對插件 <c>ExecuteAsync</c> 施加 per-tool 時限。
/// </summary>
public static class NyaToolTimeout
{
    /// <summary>
    /// 執行插件並施加逾時。逾時 → 回傳 <c>null</c>（呼叫端轉成工具失敗）；
    /// 外層 <paramref name="outerCt"/> 取消 → 照樣拋 <see cref="OperationCanceledException"/>；
    /// 插件其他例外原樣拋出，由呼叫端統一 sanitize。
    /// </summary>
    public static async Task<NyaToolResult?> RunAsync(
        INyaToolPlugin plugin, NyaToolContext ctx, TimeSpan timeout, CancellationToken outerCt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        cts.CancelAfter(timeout);
        try
        {
            return await plugin.ExecuteAsync(ctx, cts.Token);
        }
        catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
        {
            return null; // per-tool 逾時（非整體請求取消）
        }
    }
}
