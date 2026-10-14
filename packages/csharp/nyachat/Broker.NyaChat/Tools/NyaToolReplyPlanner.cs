using Broker.NyaChat.Abstractions;
using System.Text.RegularExpressions;

namespace Broker.NyaChat;

public static class NyaToolReplyPlanner
{
    private static readonly (string Action, string Claim, string Tools)[] CompletedActions =
    {
        ("記住", @"已(?:經)?(?:幫[你您]|替[你您])?(?:記住|記下)|(?:記下來|記住)了", @"^(?:note_save|remember|save_memory)$"),
        ("刪除", @"已(?:經)?(?:幫[你您]|替[你您])?(?:刪除|移除)", @"^(?:forget|(?:delete|remove)_.+)$"),
        ("建立", @"已(?:經)?(?:幫[你您]|替[你您])?建立", @"^(?:new_topic|create_.+)$"),
        ("切換", @"已(?:經)?(?:幫[你您]|替[你您])?切換", @"^(?:switch_topic|new_topic|switch_.+)$"),
        ("開新話題", @"已(?:經)?(?:幫[你您]|替[你您])?開(?:了)?(?:一個)?新話題", @"^new_topic$"),
        ("查詢", @"已(?:經)?(?:幫[你您]|替[你您])?(?:查詢|查到|查過|查好)|已(?:經)?幫[你您]查", @"^(?:search|rail|hsr|bus|flight|(?:search|query|lookup)_.+)$"),
        ("派遣", @"已(?:經)?(?:幫[你您]|替[你您])?(?:派遣|委派)", @"^(?:(?:dispatch|delegate)_.+|run_agent)$"),
        ("設定", @"已(?:經)?(?:幫[你您]|替[你您])?(?:設定|設置)", @"^(?:set_.+|configure_.+)$"),
        ("改名", @"已(?:經)?(?:幫[你您]|替[你您])?(?:改名|重新命名)", @"^rename_topic$"),
        ("遺忘", @"已(?:經)?(?:幫[你您]|替[你您])?(?:忘掉|忘記|遺忘)|(?:忘掉|忘記|遺忘)了", @"^forget$")
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

    internal static IReadOnlyList<string> FindUnbackedActionsWithPrior(string text,
        IReadOnlyList<(NyaToolCall Call, NyaToolExecStatus Status)> ledger,
        IReadOnlyList<(NyaToolCall Call, NyaToolExecStatus Status)> prior)
    {
        var actions = new HashSet<string>();
        var unquoted = Regex.Replace(text, @"「[^」]*」|『[^』]*』|“[^”]*”|""[^""]*""", "");
        foreach (Match clause in Regex.Matches(unquoted, @"[^，,。！!？?；;\n]+[，,。！!？?；;\n]?"))
        {
            if (Regex.IsMatch(clause.Value, @"[？?]|(?:嗎|麼|是否|要不要)")) continue;
            foreach (var action in CompletedActions)
                foreach (Match claim in Regex.Matches(clause.Value, action.Claim))
                {
                    var prefix = clause.Value[..claim.Index];
                    if (Regex.IsMatch(prefix, @"(?:還沒|尚未|未曾|沒有|並未|並非|不是|不能|無法|不會|未確認|如果|假如|若).*$")) continue;
                    var evidence = Regex.IsMatch(prefix, @"先前|上一輪|上次|剛才|之前|早先") ? ledger.Concat(prior) : ledger;
                    if (!evidence.Any(e => e.Status == NyaToolExecStatus.Success && Regex.IsMatch(e.Call.FunctionName, action.Tools, RegexOptions.IgnoreCase))) actions.Add(action.Action);
                }
        }
        return CompletedActions.Where(a => actions.Contains(a.Action)).Select(a => a.Action).ToList();
    }

    public static IReadOnlyList<string>? PlanDirectReplies(IReadOnlyList<NyaToolResult> results)
    {
        if (results is null || results.Count == 0)
            return null;

        var directCount = results.Count(r => r.DirectReplies is { Count: > 0 });

        if (directCount == 0)
            return null;
        if (directCount != results.Count)
            return null;

        return results.SelectMany(r => r.DirectReplies!).ToList();
    }

    public static bool IsMixed(IReadOnlyList<NyaToolResult> results)
    {
        if (results is null || results.Count == 0) return false;
        var directCount = results.Count(r => r.DirectReplies is { Count: > 0 });
        return directCount > 0 && directCount != results.Count;
    }
}
