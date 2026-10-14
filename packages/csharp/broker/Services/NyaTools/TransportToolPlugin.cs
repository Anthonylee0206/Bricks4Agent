using Broker.NyaChat.Abstractions;

namespace Broker.Services.NyaTools;

public sealed class TransportToolPlugin : INyaToolPlugin
{
    private readonly HighLevelQueryToolMediator _mediator;
    private readonly bool _personalizeOutput;

    public TransportToolPlugin(HighLevelQueryToolMediator mediator, bool personalizeOutput = true)
    {
        _mediator = mediator;
        _personalizeOutput = personalizeOutput;
    }

    public IReadOnlyList<NyaToolSchema> GetTools() => new[]
    {
        Schema("rail",   "台鐵查詢", "查詢台鐵班次時刻資訊", "我可以幫你查台鐵班次。"),
        Schema("hsr",    "高鐵查詢", "查詢高鐵班次時刻資訊", "我可以幫你查高鐵班次。"),
        Schema("bus",    "公車查詢", "查詢公車路線及班次資訊", "我可以幫你查公車路線與到站。"),
        Schema("flight", "航班查詢", "查詢航班資訊",         "我可以幫你查航班資訊。"),
    };

    private static NyaToolSchema Schema(string name, string _displayName, string description, string capability)
        => new()
        {
            Name        = name,
            Description = description,
            Group       = "transport",
            DefaultEnabled = false,
            RequiredPermission = BrokerTierToolAuthorizer.TransportPermission,
            CapabilityStatement = capability,
            Parameters = new[]
            {
                new NyaToolParam { Name = "query", Required = true, Description = "查詢條件（起訖站、時間等）" },
            }
        };

    public async Task<NyaToolResult> ExecuteAsync(NyaToolContext context, CancellationToken cancellationToken)
    {
        var query = NyaToolArgs.GetString(context.ArgumentsJson, "query");
        if (string.IsNullOrWhiteSpace(query))
            return NyaToolResult.Error("missing_query", "query", "請提供更完整的查詢條件，例如：板橋 高雄 明天上午。");

        var channel = context.ChannelType;
        var userId  = context.UserId;

        var result = context.ToolName switch
        {
            "rail"   => await _mediator.SearchRailAsync(channel, userId, query, cancellationToken),
            "hsr"    => await _mediator.SearchHsrAsync(channel, userId, query, cancellationToken),
            "bus"    => await _mediator.SearchBusAsync(channel, userId, query, cancellationToken),
            "flight" => await _mediator.SearchFlightAsync(channel, userId, query, cancellationToken),
            _        => HighLevelQueryToolResult.Fail($"未知的交通工具：{context.ToolName}", "unknown_transport_mode")
        };

        if (!result.Success || string.IsNullOrWhiteSpace(result.Reply))
            return NyaToolResult.Error("transport_failed", "query", "無法取得查詢結果，請稍後再試。");

        return _personalizeOutput
            ? NyaToolResult.Envelope(new { reply = result.Reply })
            : NyaToolResult.Envelope(new { reply = result.Reply }, directReplies: new[] { result.Reply });
    }
}
