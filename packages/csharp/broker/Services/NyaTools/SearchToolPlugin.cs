using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.Services.NyaTools;

/// <summary>
/// broker adapter 插件：網路搜尋（大項四 4A）。把既有 <see cref="HighLevelQueryToolMediator"/>
/// 的搜尋能力包成 <see cref="INyaToolPlugin"/>，讓 Nya 在內部完成「工具 → 結果 → LLM 二次回覆」閉環。
/// </summary>
/// <remarks>
/// 相依（mediator）由本插件<b>自身建構式 DI</b> 取得（gaps 0.2）；Nya 核心不需知道。
/// 本插件屬 broker 端（mediator 是 broker 型別，不可進 Nya 核心），僅實作 <c>Abstractions</c> 契約。
/// <para>
/// D2：<paramref name="personalizeOutput"/> = true（預設）→ 回 <see cref="NyaToolResult.LlmContent"/>，
/// 由 Orchestrator 走二次 LLM 個性化；false → 另帶 <see cref="NyaToolResult.DirectReplies"/>，
/// 單一工具時 Nya 會略過二次 LLM、維持 worker 原樣輸出。
/// </para>
/// </remarks>
public sealed class SearchToolPlugin : INyaToolPlugin
{
    private readonly HighLevelQueryToolMediator _mediator;
    private readonly bool _personalizeOutput;

    public SearchToolPlugin(HighLevelQueryToolMediator mediator, bool personalizeOutput = true)
    {
        _mediator = mediator;
        _personalizeOutput = personalizeOutput;
    }

    public IReadOnlyList<NyaToolSchema> GetTools() => new[]
    {
        new NyaToolSchema
        {
            Name        = "search",
            Description = "執行即時網路搜尋，取得最新資訊",
            Group       = "search",
            DefaultEnabled = false,
            RequiredPermission = BrokerTierToolAuthorizer.QueryPermission,
            CapabilityStatement = "我可以幫你做即時網路搜尋並彙整結果。",
            Parameters = new[]
            {
                new NyaToolParam { Name = "query", Required = true, Description = "搜尋的關鍵字" },
            }
        }
    };

    public async Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken cancellationToken)
    {
        var query = NyaToolArgs.GetString(context.ArgumentsJson, "query");
        if (string.IsNullOrWhiteSpace(query))
            return NyaToolResult.Fail(FailJson("search_query_missing", "請提供要搜尋的關鍵字。"));

        var result = await _mediator.SearchWebAsync(context.ChannelType, context.UserId, query, cancellationToken);
        return Map(result, _personalizeOutput);
    }

    /// <summary>
    /// 把 mediator 結果翻譯成 <see cref="NyaToolResult"/>（#2 契約修復）。
    /// </summary>
    /// <remarks>
    /// 關鍵修復：mediator 在<b>零結果</b>時仍回 <c>Success=true</c> 並帶罐頭文字
    /// （「沒有找到任何可用結果…」），舊版直接 <c>Ok(reply)</c> 餵回 LLM，使 LLM 把罐頭文字
    /// 當成有效答案、給出錯誤回應。改判定<b>結構化 <see cref="HighLevelQueryToolResult.Results"/></b>：
    /// 失敗或零結果一律回 <see cref="NyaToolResult.Fail"/> 並帶<b>錯誤碼</b>（search_failed /
    /// search_no_results），讓 LLM 依錯誤碼誠實回應，而非沿用罐頭文字。
    /// 純函式、與 mediator/DI 解耦，供單元測試直接驗證契約。
    /// </remarks>
    internal static NyaToolResult Map(HighLevelQueryToolResult result, bool personalizeOutput)
    {
        if (!result.Success)
            return NyaToolResult.Fail(FailJson(
                string.IsNullOrWhiteSpace(result.Error) ? "search_failed" : result.Error!,
                "搜尋失敗，請告知使用者目前無法取得搜尋結果。"));

        // 零結果不是有效答案：標記為錯誤並給錯誤碼，避免 LLM 沿用罐頭「沒有找到任何可用結果」文字。
        if (result.Results.Count == 0 || string.IsNullOrWhiteSpace(result.Reply))
            return NyaToolResult.Fail(FailJson(
                "search_no_results",
                "搜尋沒有找到相關結果，請誠實告知使用者，並可建議調整關鍵字或補充更具體條件。"));

        return personalizeOutput
            ? NyaToolResult.Ok(result.Reply)
            : NyaToolResult.Ok(result.Reply, new[] { result.Reply });
    }

    /// <summary>建構回饋給 LLM 的結構化失敗 JSON（含錯誤碼 + 安全訊息，escape 後內嵌）。</summary>
    private static string FailJson(string error, string message)
    {
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return $"{{\"success\":false,\"error\":\"{Esc(error)}\",\"message\":\"{Esc(message)}\"}}";
    }
}
