using Broker.NyaChat.Abstractions;

namespace Broker.Services.NyaTools;

/// <summary>
/// broker adapter 插件：交通查詢（大項四 4A）。<b>一個插件暴露 4 個工具</b>
/// （rail / hsr / bus / flight），示範「插件:工具 = 一對多」，依 <see cref="NyaToolContext.ToolName"/> 分派。
/// </summary>
/// <remarks>
/// 包 <see cref="HighLevelQueryToolMediator"/> 的台鐵 / 高鐵 / 公車 / 航班查詢。
/// D2：<paramref name="personalizeOutput"/> 控制走二次 LLM 個性化（預設）或維持 worker 原樣輸出。
/// </remarks>
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
            return NyaToolResult.Fail("請提供更完整的查詢條件，例如：板橋 高雄 明天上午。");

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
            return NyaToolResult.Fail($"[交通查詢失敗] {result.Error ?? "無法取得查詢結果，請稍後再試。"}");

        return _personalizeOutput
            ? NyaToolResult.Ok(result.Reply)
            : NyaToolResult.Ok(result.Reply, new[] { result.Reply });
    }
}
