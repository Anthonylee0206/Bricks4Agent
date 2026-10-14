using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.Services.NyaTools;

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
            return NyaToolResult.Error("search_query_missing", "query", "請提供要搜尋的關鍵字。");

        var result = await _mediator.SearchWebAsync(context.ChannelType, context.UserId, query, cancellationToken);
        return Map(result, _personalizeOutput);
    }

    internal static NyaToolResult Map(HighLevelQueryToolResult result, bool personalizeOutput)
    {
        if (!result.Success)
            return NyaToolResult.Error(
                string.IsNullOrWhiteSpace(result.Error) ? "search_failed" : result.Error!,
                "query", "搜尋失敗，請告知使用者目前無法取得搜尋結果。");

        if (result.Results.Count == 0 || string.IsNullOrWhiteSpace(result.Reply))
            return NyaToolResult.Error(
                "search_no_results",
                "query", "搜尋沒有找到相關結果，請誠實告知使用者，並可建議調整關鍵字或補充更具體條件。");

        return personalizeOutput
            ? NyaToolResult.Envelope(new { reply = result.Reply, results = result.Results })
            : NyaToolResult.Envelope(new { reply = result.Reply, results = result.Results }, directReplies: new[] { result.Reply });
    }

}
