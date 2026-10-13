using Broker.NyaChat.Abstractions;
using System.Text.RegularExpressions;

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
    private static readonly (string Action, string Claim, string Tools)[] CompletedActions =
    {
        ("記住", @"已(?:經)?(?:幫[你您]|替[你您])?(?:記住|記下)|(?:記下來|記住)了", @"^(?:upsert_fact|remember|save_memory)$"),
        ("刪除", @"已(?:經)?(?:幫[你您]|替[你您])?(?:刪除|移除)", @"^(?:delete_fact|(?:delete|remove)_.+)$"),
        ("建立", @"已(?:經)?(?:幫[你您]|替[你您])?建立", @"^(?:new_topic|create_.+)$"),
        ("切換", @"已(?:經)?(?:幫[你您]|替[你您])?切換", @"^(?:switch_topic|new_topic|switch_.+)$"),
        ("開新話題", @"已(?:經)?(?:幫[你您]|替[你您])?開(?:了)?(?:一個)?新話題", @"^new_topic$"),
        ("查詢", @"已(?:經)?(?:幫[你您]|替[你您])?(?:查詢|查到|查過|查好)|已(?:經)?幫[你您]查", @"^(?:search|rail|hsr|bus|flight|(?:search|query|lookup)_.+)$"),
        ("派遣", @"已(?:經)?(?:幫[你您]|替[你您])?(?:派遣|委派)", @"^(?:(?:dispatch|delegate)_.+|run_agent)$"),
        ("設定", @"已(?:經)?(?:幫[你您]|替[你您])?(?:設定|設置)", @"^(?:set_.+|configure_.+)$"),
        ("改名", @"已(?:經)?(?:幫[你您]|替[你您])?(?:改名|重新命名)", @"^rename_topic$"),
        ("遺忘", @"已(?:經)?(?:幫[你您]|替[你您])?(?:忘掉|忘記|遺忘)|(?:忘掉|忘記|遺忘)了", @"^delete_fact$")
    };

    internal static IReadOnlyList<string> DetectCompletedActions(string text)
    {
        var actions = new HashSet<string>();
        var unquoted = Regex.Replace(text, @"「[^」]*」|『[^』]*』|“[^”]*”|""[^""]*""", "");
        foreach (Match clause in Regex.Matches(unquoted, @"[^，,。！!？?；;\n]+[，,。！!？?；;\n]?"))
        {
            var value = clause.Value;
            if (Regex.IsMatch(value, @"[？?]|(?:嗎|麼|是否|要不要)")) continue;
            foreach (var action in CompletedActions)
                foreach (Match claim in Regex.Matches(value, action.Claim))
                    if (!Regex.IsMatch(value[..claim.Index], @"(?:還沒|尚未|未曾|沒有|並未|並非|不是|不能|無法|不會|未確認|如果|假如|若).*$"))
                        actions.Add(action.Action);
        }
        return CompletedActions.Where(a => actions.Contains(a.Action)).Select(a => a.Action).ToList();
    }

    internal static IReadOnlyList<string> FindUnbackedActions(string text, IReadOnlyList<(NyaToolCall Call, NyaToolExecStatus Status)> ledger)
    {
        var detected = DetectCompletedActions(text);
        return CompletedActions.Where(a => detected.Contains(a.Action) &&
            !ledger.Any(e => e.Status == NyaToolExecStatus.Success &&
                Regex.IsMatch(e.Call.FunctionName, a.Tools, RegexOptions.IgnoreCase)))
            .Select(a => a.Action).ToList();
    }


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
