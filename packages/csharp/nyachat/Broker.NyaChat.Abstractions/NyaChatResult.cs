namespace Broker.NyaChat.Abstractions;

/// <summary>
/// 通道無關的對話結果。各通道 adapter 依自身需求格式化 <see cref="Replies"/>。
/// </summary>
/// <remarks>
/// Orchestrator 已採用本型別，並在內部完成「工具 → 結果 → LLM 二次回覆」的閉環。
/// 移除原本的過渡欄位 <c>PendingTool</c>（外部工具不再由消費端執行）；<see cref="Replies"/> 即最終回覆。
/// </remarks>
public sealed class NyaChatResult
{
    /// <summary>回覆內容（可能因長度切割為多段）。</summary>
    public IReadOnlyList<string> Replies { get; init; } = Array.Empty<string>();

    /// <summary>錯誤訊息（null = 成功）。</summary>
    public string? Error { get; init; }

    /// <summary>目前使用者的有效訊息數（未刪除、未摘要）。</summary>
    public int HistoryCount { get; init; }

    /// <summary>當前話題 ID（供通道層通知使用）。</summary>
    public string? TopicId { get; init; }
}
