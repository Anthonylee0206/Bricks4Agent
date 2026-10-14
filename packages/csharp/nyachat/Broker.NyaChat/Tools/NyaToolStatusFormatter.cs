using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

public enum NyaToolExecStatus
{

    Success,

    Failed,

    NotEnabled,

    Denied,
    NeedsConfirmation,
    Noop,
    Partial
}

public static class NyaToolStatusFormatter
{

    public static NyaToolExecStatus FromResult(NyaToolResult result)
        => !result.Success ? FailureStatus(result) : result.Outcome switch
        {
            "needs_confirmation" => NyaToolExecStatus.NeedsConfirmation,
            "noop" => NyaToolExecStatus.Noop,
            "partial" => NyaToolExecStatus.Partial,
            _ => NyaToolExecStatus.Success
        };

    private static NyaToolExecStatus FailureStatus(NyaToolResult result)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(result.LlmContent);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == System.Text.Json.JsonValueKind.Object && error.TryGetProperty("code", out var code))
                return code.GetString() switch { "not_authorized" => NyaToolExecStatus.Denied, "disabled" or "unknown_tool" or "external_scope" => NyaToolExecStatus.NotEnabled, _ => NyaToolExecStatus.Failed };
        }
        catch (System.Text.Json.JsonException) { }
        return NyaToolExecStatus.Failed;
    }

    public static string Label(NyaToolExecStatus status) => status switch
    {
        NyaToolExecStatus.Success    => "成功",
        NyaToolExecStatus.Failed     => "失敗（工具已執行但未成功）",
        NyaToolExecStatus.NotEnabled => "未啟動（功能關閉／無權限／不存在，並未實際執行）",
        NyaToolExecStatus.Denied     => "未啟動（權限不足，並未實際執行）",
        NyaToolExecStatus.NeedsConfirmation => "候選待確認",
        NyaToolExecStatus.Noop => "目標狀態已滿足（未新增變更）",
        NyaToolExecStatus.Partial => "部分完成（依逐項效果報告）",
        _                            => "未知"
    };

    public static string FormatForLlm(NyaToolExecStatus status, string content)
    {
        var body = string.IsNullOrEmpty(content) ? "(no content)" : content;
        return $"[工具執行狀態：{Label(status)}]\n{body}";
    }

    public static string FormatFallback(IEnumerable<NyaToolExecStatus> statuses)
    {
        var counts = statuses.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        if (counts.Count == 0) return "這次沒有執行任何工具操作。";
        var parts = new List<string>();
        foreach (var status in new[] { NyaToolExecStatus.Success, NyaToolExecStatus.Failed, NyaToolExecStatus.Denied, NyaToolExecStatus.NotEnabled,
                     NyaToolExecStatus.NeedsConfirmation, NyaToolExecStatus.Noop, NyaToolExecStatus.Partial })
        {
            if (!counts.TryGetValue(status, out var count)) continue;
            var outcome = status switch
            {
                NyaToolExecStatus.Success => "操作成功",
                NyaToolExecStatus.Failed => "操作失敗",
                NyaToolExecStatus.Denied => "因權限不足未執行",
                NyaToolExecStatus.NeedsConfirmation => "候選待確認",
                NyaToolExecStatus.Noop => "目標狀態已滿足，未新增變更",
                NyaToolExecStatus.Partial => "部分完成，須逐項確認效果",
                _ => "因工具未啟用或不存在而未執行"
            };
            parts.Add($"{count} 項{outcome}");
        }
        return $"這次有{string.Join("、", parts)}；詳細結果暫時無法整理，請稍後再試。";
    }
}
