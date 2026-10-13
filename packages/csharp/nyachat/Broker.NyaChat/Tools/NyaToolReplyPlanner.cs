using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 決定一輪工具呼叫後是否「維持原樣輸出」（D2 DirectReplies，略過二次 LLM）（Bug #7 嚴審修復）。
/// </summary>
/// <remarks>
/// 舊邏輯只在 <c>executed.Count == 1</c> 時 honor DirectReplies；多工具時即使每個插件都明確要求
/// 原樣輸出，DirectReplies 也會被靜默丟棄、強制走二次 LLM 個性化——違反插件契約。
/// 新規則：
/// <list type="bullet">
///   <item>沒有任何結果帶 DirectReplies → 回 null（走閉環二次 LLM）。</item>
///   <item><b>每個</b>結果都帶 DirectReplies → 依序串接全部、原樣輸出（不丟任何一段）。</item>
///   <item>混合（部分原樣、部分個性化）→ 回 null 走閉環；非靜默遺失，因插件同時也帶 LlmContent，
///         由呼叫端記 log 提示。</item>
/// </list>
/// </remarks>
public static class NyaToolReplyPlanner
{
    /// <summary>回傳要原樣輸出的回覆（略過二次 LLM）；null = 走閉環。</summary>
    public static IReadOnlyList<string>? PlanDirectReplies(IReadOnlyList<NyaToolResult> results)
    {
        if (results is null || results.Count == 0)
            return null;

        var directCount = results.Count(r => r.DirectReplies is { Count: > 0 });

        if (directCount == 0)
            return null;                       // 全部個性化 → 閉環
        if (directCount != results.Count)
            return null;                       // 混合 → 閉環（呼叫端記 log）

        return results.SelectMany(r => r.DirectReplies!).ToList();
    }

    /// <summary>是否為「混合」情境（部分帶 DirectReplies、部分沒有）；供呼叫端記 log。</summary>
    public static bool IsMixed(IReadOnlyList<NyaToolResult> results)
    {
        if (results is null || results.Count == 0) return false;
        var directCount = results.Count(r => r.DirectReplies is { Count: > 0 });
        return directCount > 0 && directCount != results.Count;
    }
}
