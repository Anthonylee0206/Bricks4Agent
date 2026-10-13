namespace Broker.NyaChat.Abstractions;

/// <summary>
/// 工具執行結果。
/// </summary>
/// <remarks>
/// 閉環語意：<see cref="LlmContent"/> 會以 <c>role=tool</c> 追加回對話，
/// 觸發第二次 LLM 呼叫產生個性化自然語言回覆（「我的損益如何？」 → 工具結果 → LLM → 回覆）。
/// <see cref="DirectReplies"/> 為可選的「略過 LLM、直接回給使用者」內容（如長表格、工件）。
/// </remarks>
public sealed class NyaToolResult
{
    /// <summary>工具是否成功執行。</summary>
    public bool Success { get; init; }

    /// <summary>回饋給 LLM 做自然語言二次回覆的內容。</summary>
    public string LlmContent { get; init; } = "";

    /// <summary>可選：略過 LLM 直接回給使用者的訊息（可多段）。null = 一律走二次 LLM 回覆。</summary>
    public IReadOnlyList<string>? DirectReplies { get; init; }

    /// <summary>成功結果便利建構子。</summary>
    public static NyaToolResult Ok(string llmContent, IReadOnlyList<string>? directReplies = null)
        => new() { Success = true, LlmContent = llmContent, DirectReplies = directReplies };

    /// <summary>失敗結果便利建構子（訊息仍回饋給 LLM，使其能向使用者說明失敗原因）。</summary>
    public static NyaToolResult Fail(string llmContent)
        => new() { Success = false, LlmContent = llmContent };
}
