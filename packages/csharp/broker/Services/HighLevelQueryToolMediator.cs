using System.Text.Json;
using BrokerCore.Contracts;
using BrokerCore.Contracts.Transport;
using BrokerCore.Services;

namespace Broker.Services;

public sealed class HighLevelQueryToolMediator
{
    private const string GoogleToolId = "web.search.google";
    private const string DuckDuckGoToolId = "web.search.duckduckgo";
    private const string WikipediaToolId = "knowledge.wikipedia.search";
    private const string TransportToolId = "transport.query";

    private readonly IToolSpecRegistry _toolSpecRegistry;
    private readonly IExecutionDispatcher _dispatcher;
    private readonly ILogger<HighLevelQueryToolMediator> _logger;

    public HighLevelQueryToolMediator(
        IToolSpecRegistry toolSpecRegistry,
        IExecutionDispatcher dispatcher,
        ILogger<HighLevelQueryToolMediator> logger)
    {
        _toolSpecRegistry = toolSpecRegistry;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<HighLevelQueryToolResult> SearchWebAsync(
        string channel,
        string userId,
        string query,
        CancellationToken cancellationToken = default)
    {
        _ = channel;
        _ = userId;
        _ = cancellationToken;

        if (string.IsNullOrWhiteSpace(query))
        {
            return HighLevelQueryToolResult.Fail(
                "請在 search 後面提供要查詢的關鍵字，例如：search 台北天氣",
                "search_query_missing");
        }

        var googleResult = await SearchWebWithToolAsync(GoogleToolId, query);
        // #1 修復：Google 以 gbv=1 抓取常回 HTTP 200 但「零可解析結果」（Success=true、Results 空），
        // 舊邏輯只在 Google「硬失敗」時才退 DuckDuckGo，導致零結果時直接回罐頭文字、永不退 DDG。
        // 改為「成功且有結果」才直接採用，否則（失敗 or 零結果）一律退到 DuckDuckGo。
        if (googleResult.Success && googleResult.Results.Count > 0)
            return googleResult;

        _logger.LogWarning(
            "High-level Google web search yielded {State}; falling back to DuckDuckGo.",
            googleResult.Success ? "no parseable results" : (googleResult.Error ?? "unknown_error"));

        var fallbackResult = await SearchWebWithToolAsync(DuckDuckGoToolId, query);

        if (fallbackResult.Success && fallbackResult.Results.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(googleResult.Error))
            {
                fallbackResult.Reply = string.Join('\n', new[]
                {
                    "Google 搜尋暫時失敗，已改用 DuckDuckGo。",
                    string.Empty,
                    fallbackResult.Reply
                });
            }
            return fallbackResult;
        }

        // 兩者皆無可用結果：任一為硬失敗 → 回報失敗錯誤碼；
        // 否則兩者皆「成功但零結果」→ 回傳零結果（Success=true、Results 空），
        // 交由上層 SearchToolPlugin.Map 轉成 search_no_results 錯誤碼（#2），避免 LLM 沿用罐頭文字。
        if (!googleResult.Success || !fallbackResult.Success)
        {
            var err = !string.IsNullOrWhiteSpace(googleResult.Error) ? googleResult.Error
                    : !string.IsNullOrWhiteSpace(fallbackResult.Error) ? fallbackResult.Error
                    : "tool_dispatch_failed";
            return HighLevelQueryToolResult.Fail($"網路搜尋失敗：{err}", err!);
        }

        return fallbackResult;
    }

    public Task<HighLevelQueryToolResult> SearchWikipediaAsync(
        string channel,
        string userId,
        string query,
        CancellationToken cancellationToken = default)
    {
        _ = channel;
        _ = userId;
        _ = cancellationToken;

        if (string.IsNullOrWhiteSpace(query))
        {
            return Task.FromResult(HighLevelQueryToolResult.Fail(
                "請在 Wikipedia 查詢後面提供關鍵字。",
                "wikipedia_query_missing"));
        }

        return SearchReadOnlyToolAsync(
            WikipediaToolId,
            query,
            locale: "zh-TW",
            limit: 5,
            safeMode: null,
            unavailableMessage: "目前無法使用 Wikipedia 查詢。",
            bindingUnavailableMessage: "目前無法使用 broker-mediated Wikipedia 查詢路徑。",
            dispatchFailurePrefix: "Wikipedia 查詢失敗：");
    }

    public Task<HighLevelQueryToolResult> SearchRailAsync(
        string channel,
        string userId,
        string query,
        CancellationToken cancellationToken = default)
        => SearchTransportAsync(channel, userId, "rail", query, cancellationToken);

    public Task<HighLevelQueryToolResult> SearchHsrAsync(
        string channel,
        string userId,
        string query,
        CancellationToken cancellationToken = default)
        => SearchTransportAsync(channel, userId, "hsr", query, cancellationToken);

    public Task<HighLevelQueryToolResult> SearchBusAsync(
        string channel,
        string userId,
        string query,
        CancellationToken cancellationToken = default)
        => SearchTransportAsync(channel, userId, "bus", query, cancellationToken);

    public Task<HighLevelQueryToolResult> SearchFlightAsync(
        string channel,
        string userId,
        string query,
        CancellationToken cancellationToken = default)
        => SearchTransportAsync(channel, userId, "flight", query, cancellationToken);

    private Task<HighLevelQueryToolResult> SearchWebWithToolAsync(string toolId, string query)
        => SearchReadOnlyToolAsync(
            toolId,
            query,
            locale: "zh-TW",
            limit: 5,
            safeMode: "moderate",
            unavailableMessage: "目前無法使用網路搜尋。",
            bindingUnavailableMessage: "目前無法使用 broker-mediated web search route。",
            dispatchFailurePrefix: "網路搜尋失敗：");

    private async Task<HighLevelQueryToolResult> SearchReadOnlyToolAsync(
        string toolId,
        string query,
        string locale,
        int limit,
        string? safeMode,
        string unavailableMessage,
        string bindingUnavailableMessage,
        string dispatchFailurePrefix)
    {
        var spec = _toolSpecRegistry.Get(toolId);
        if (spec == null || !string.Equals(spec.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return HighLevelQueryToolResult.Fail(unavailableMessage, "tool_spec_unavailable");
        }

        var binding = spec.CapabilityBindings.FirstOrDefault(candidate =>
            string.Equals(candidate.CapabilityId, toolId, StringComparison.OrdinalIgnoreCase));
        if (binding == null || !binding.Registered || string.IsNullOrWhiteSpace(binding.Route))
        {
            return HighLevelQueryToolResult.Fail(bindingUnavailableMessage, "tool_binding_unavailable");
        }

        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["query"] = query,
            ["locale"] = locale,
            ["limit"] = limit
        };
        if (!string.IsNullOrWhiteSpace(safeMode))
            args["safe_mode"] = safeMode;

        ApprovedRequest.WarnIfBypass();
        var approvedRequest = new ApprovedRequest
        {
            RequestId = $"hlq_{Guid.NewGuid():N}"[..18],
            CapabilityId = binding.CapabilityId,
            Route = binding.Route,
            PrincipalId = "system:high-level-coordinator",
            TaskId = "global",
            SessionId = "high-level-query",
            Scope = "{}",
            Payload = JsonSerializer.Serialize(new
            {
                route = binding.Route,
                args
            })
        };

        var executionResult = await _dispatcher.DispatchAsync(approvedRequest);
        if (!executionResult.Success || string.IsNullOrWhiteSpace(executionResult.ResultPayload))
        {
            var error = executionResult.ErrorMessage ?? "tool_dispatch_failed";
            _logger.LogWarning("High-level read-only tool {ToolId} failed: {Error}", toolId, error);
            return HighLevelQueryToolResult.Fail($"{dispatchFailurePrefix}{error}", "tool_dispatch_failed");
        }

        try
        {
            using var doc = JsonDocument.Parse(executionResult.ResultPayload);
            var engine = doc.RootElement.TryGetProperty("engine", out var engineNode)
                ? engineNode.GetString() ?? toolId
                : toolId;
            var returnedQuery = doc.RootElement.TryGetProperty("query", out var queryNode)
                ? queryNode.GetString() ?? query
                : query;

            var items = new List<HighLevelQuerySearchResult>();
            if (doc.RootElement.TryGetProperty("results", out var resultsNode) &&
                resultsNode.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in resultsNode.EnumerateArray())
                {
                    items.Add(new HighLevelQuerySearchResult
                    {
                        Rank = item.TryGetProperty("rank", out var rankNode) && rankNode.TryGetInt32(out var rank)
                            ? rank
                            : items.Count + 1,
                        Title = item.TryGetProperty("title", out var titleNode)
                            ? titleNode.GetString() ?? string.Empty
                            : string.Empty,
                        Url = item.TryGetProperty("url", out var urlNode)
                            ? urlNode.GetString() ?? string.Empty
                            : string.Empty,
                        Snippet = item.TryGetProperty("snippet", out var snippetNode)
                            ? snippetNode.GetString() ?? string.Empty
                            : string.Empty
                    });
                }
            }

            return new HighLevelQueryToolResult
            {
                Success = true,
                Reply = BuildSearchReply(engine, returnedQuery, items),
                ToolId = toolId,
                Engine = engine,
                Results = items
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse high-level query tool result for {ToolId}", toolId);
            return HighLevelQueryToolResult.Fail("查詢結果解析失敗。", "tool_result_parse_failed");
        }
    }

    private async Task<HighLevelQueryToolResult> SearchTransportAsync(
        string channel,
        string userId,
        string transportMode,
        string query,
        CancellationToken cancellationToken = default)
    {
        _ = userId;
        _ = cancellationToken;

        if (string.IsNullOrWhiteSpace(query))
        {
            return HighLevelQueryToolResult.Fail(
                "請提供更完整的查詢條件，例如：?rail 板橋 高雄 明天上午",
                "transport_query_missing");
        }

        var spec = _toolSpecRegistry.Get(TransportToolId);
        if (spec == null || !string.Equals(spec.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return HighLevelQueryToolResult.Fail("目前無法使用交通查詢。", "transport_tool_unavailable");
        }

        var binding = spec.CapabilityBindings.FirstOrDefault(candidate =>
            string.Equals(candidate.CapabilityId, TransportToolId, StringComparison.OrdinalIgnoreCase));
        if (binding == null || !binding.Registered || string.IsNullOrWhiteSpace(binding.Route))
        {
            return HighLevelQueryToolResult.Fail(
                "目前無法使用 broker-mediated 交通查詢路徑。",
                "transport_binding_unavailable");
        }

        var approvedRequest = new ApprovedRequest
        {
            RequestId = $"hlt_{Guid.NewGuid():N}"[..18],
            CapabilityId = binding.CapabilityId,
            Route = binding.Route,
            PrincipalId = "system:high-level-coordinator",
            TaskId = "global",
            SessionId = "high-level-query",
            Scope = "{}",
            Payload = JsonSerializer.Serialize(new
            {
                route = binding.Route,
                args = new
                {
                    transport_mode = transportMode,
                    user_query = query,
                    locale = "zh-TW",
                    channel
                }
            })
        };

        var executionResult = await _dispatcher.DispatchAsync(approvedRequest);
        if (!executionResult.Success || string.IsNullOrWhiteSpace(executionResult.ResultPayload))
        {
            var error = executionResult.ErrorMessage ?? "transport_tool_dispatch_failed";
            _logger.LogWarning("High-level transport search failed for {Mode}: {Error}", transportMode, error);
            return HighLevelQueryToolResult.Fail($"交通查詢失敗：{error}", "transport_tool_dispatch_failed");
        }

        try
        {
            var transportResponse = JsonSerializer.Deserialize<TransportQueryResponse>(executionResult.ResultPayload);
            if (transportResponse == null)
                throw new InvalidOperationException("transport response deserialized to null");

            var items = new List<HighLevelQuerySearchResult>();
            if (transportResponse.ResultTypeValue == TransportResultType.FinalAnswer)
            {
                var rank = 1;
                foreach (var record in transportResponse.Records)
                {
                    items.Add(new HighLevelQuerySearchResult
                    {
                        Rank = rank++,
                        Title = record.GetValueOrDefault("title")?.ToString() ?? string.Empty,
                        Snippet = record.GetValueOrDefault("snippet")?.ToString() ?? string.Empty
                    });
                }
            }

            return new HighLevelQueryToolResult
            {
                Success = true,
                Reply = BuildTransportContractReply(transportResponse),
                ToolId = TransportToolId,
                Engine = transportMode,
                Results = items
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse high-level transport result for {Mode}", transportMode);
            return HighLevelQueryToolResult.Fail("交通查詢結果解析失敗。", "transport_result_parse_failed");
        }
    }

    internal static string BuildTransportContractReply(TransportQueryResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Answer))
        {
            var lines = new List<string> { response.Answer };
            if (response.ResultTypeValue == TransportResultType.NeedFollowUp && response.FollowUp != null)
            {
                lines.Add(string.Empty);
                lines.Add(response.FollowUp.Question);
                foreach (var option in response.FollowUp.Options)
                {
                    lines.Add($"- {option.Label}");
                }
            }

            return string.Join('\n', lines);
        }

        return response.ResultTypeValue switch
        {
            TransportResultType.NeedFollowUp => "我還需要補充幾項資訊，才能繼續查詢。",
            TransportResultType.RangeAnswer => "目前先依較寬條件整理結果。",
            _ => "目前沒有取得可用結果。"
        };
    }

    internal static string BuildSearchReply(
        string engine,
        string query,
        IReadOnlyList<HighLevelQuerySearchResult> results)
    {
        var lines = new List<string>
        {
            $"搜尋來源：{engine} / {query}",
            string.Empty
        };

        if (results.Count == 0)
        {
            lines.Add("沒有找到任何可用結果。");
            lines.Add("1. 調整關鍵詞");
            lines.Add("2. 補充更具體的地點、時間或名詞");
            lines.Add("3. 試試英文搜尋");
            return string.Join('\n', lines);
        }

        foreach (var result in results)
        {
            lines.Add($"{result.Rank}. {result.Title}");
            if (!string.IsNullOrWhiteSpace(result.Snippet))
                lines.Add(result.Snippet);
            if (!string.IsNullOrWhiteSpace(result.Url))
                lines.Add(result.Url);
            lines.Add(string.Empty);
        }

        return string.Join('\n', lines.Where(line => line != null));
    }

    internal static string BuildTransportReply(
        string mode,
        string query,
        IReadOnlyList<HighLevelQuerySearchResult> results,
        string sources,
        string retrievedAt)
    {
        var lines = new List<string>
        {
            $"交通查詢：{mode} / {query}"
        };

        if (!string.IsNullOrWhiteSpace(retrievedAt))
            lines.Add($"查詢時間：{retrievedAt}");
        if (!string.IsNullOrWhiteSpace(sources))
            lines.Add($"來源：{sources}");

        lines.Add(string.Empty);

        if (results.Count == 0)
        {
            lines.Add("沒有找到任何可用結果。");
            lines.Add("你可以改用更完整的查詢條件，例如：");
            lines.Add("?rail 台北 台中 今天 18:00");
            return string.Join('\n', lines);
        }

        foreach (var result in results)
        {
            lines.Add($"{result.Rank}. {result.Title}");
            if (!string.IsNullOrWhiteSpace(result.Snippet))
                lines.Add(result.Snippet);
            if (!string.IsNullOrWhiteSpace(result.Url))
                lines.Add(result.Url);
            lines.Add(string.Empty);
        }

        return string.Join('\n', lines.Where(line => line != null));
    }

    private static List<HighLevelQuerySearchResult> ParseTdxTimetableResult(JsonElement tdxNode)
    {
        var items = new List<HighLevelQuerySearchResult>();

        var listNode = tdxNode.TryGetProperty("trains", out var trainsNode) && trainsNode.ValueKind == JsonValueKind.Array
            ? trainsNode
            : tdxNode.TryGetProperty("flights", out var flightsNode) && flightsNode.ValueKind == JsonValueKind.Array
                ? flightsNode
                : tdxNode.TryGetProperty("buses", out var busesNode) && busesNode.ValueKind == JsonValueKind.Array
                    ? busesNode
                    : default;

        if (listNode.ValueKind != JsonValueKind.Array)
            return items;

        var isFlight = tdxNode.TryGetProperty("flights", out _);
        var isBus = tdxNode.TryGetProperty("buses", out _);
        var rank = 1;

        foreach (var entry in listNode.EnumerateArray())
        {
            string title;
            string snippet;

            if (isFlight)
            {
                var flightNo = entry.TryGetProperty("flight_no", out var fnNode) ? fnNode.GetString() ?? string.Empty : string.Empty;
                var departure = entry.TryGetProperty("departure_time", out var depNode) ? depNode.GetString() ?? string.Empty : string.Empty;
                var arrival = entry.TryGetProperty("arrival_time", out var arrNode) ? arrNode.GetString() ?? string.Empty : string.Empty;
                var status = entry.TryGetProperty("status", out var stNode) ? stNode.GetString() ?? string.Empty : string.Empty;
                title = $"航班 {flightNo}";
                snippet = string.IsNullOrWhiteSpace(status)
                    ? $"{departure} 起飛 / {arrival} 抵達"
                    : $"{departure} 起飛 / {arrival} 抵達（{status}）";
            }
            else if (isBus)
            {
                var routeName = entry.TryGetProperty("route_name", out var routeNode) ? routeNode.GetString() ?? string.Empty : string.Empty;
                var stopName = entry.TryGetProperty("stop_name", out var stopNode) ? stopNode.GetString() ?? string.Empty : string.Empty;
                var direction = entry.TryGetProperty("direction", out var directionNode) ? directionNode.GetString() ?? string.Empty : string.Empty;
                var estimateMinutes = entry.TryGetProperty("estimate_minutes", out var etaNode) && etaNode.TryGetInt32(out var eta)
                    ? eta
                    : -1;
                var stopStatus = entry.TryGetProperty("stop_status", out var statusNode) && statusNode.TryGetInt32(out var busStatus)
                    ? busStatus
                    : -1;
                title = string.IsNullOrWhiteSpace(routeName) ? "公車" : $"公車 {routeName}";
                snippet = estimateMinutes >= 0
                    ? $"{stopName} / 約 {estimateMinutes} 分鐘"
                    : $"{stopName} / {FormatBusStopStatus(stopStatus)}";
                if (!string.IsNullOrWhiteSpace(direction))
                    snippet = $"{snippet}（{direction}）";
            }
            else
            {
                var trainNo = entry.TryGetProperty("train_no", out var noNode) ? noNode.GetString() ?? string.Empty : string.Empty;
                var trainType = entry.TryGetProperty("train_type", out var typeNode) ? typeNode.GetString() ?? string.Empty : string.Empty;
                var departure = entry.TryGetProperty("departure_time", out var depNode) ? depNode.GetString() ?? string.Empty : string.Empty;
                var arrival = entry.TryGetProperty("arrival_time", out var arrNode) ? arrNode.GetString() ?? string.Empty : string.Empty;
                title = string.IsNullOrWhiteSpace(trainType) ? $"車次 {trainNo}" : $"{trainType} {trainNo}";
                snippet = $"{departure} 發車 / {arrival} 抵達";
            }

            items.Add(new HighLevelQuerySearchResult
            {
                Rank = rank++,
                Title = title,
                Snippet = snippet
            });
        }

        return items;
    }

    internal static string BuildTdxTimetableReply(
        string mode,
        string query,
        JsonElement tdxNode,
        string sources,
        string retrievedAt)
    {
        var origin = tdxNode.TryGetProperty("origin", out var origNode) ? origNode.GetString() ?? string.Empty : string.Empty;
        var destination = tdxNode.TryGetProperty("destination", out var destNode) ? destNode.GetString() ?? string.Empty : string.Empty;
        var date = tdxNode.TryGetProperty("date", out var dateNode) ? dateNode.GetString() ?? string.Empty : string.Empty;
        var trainCount = tdxNode.TryGetProperty("train_count", out var countNode) && countNode.TryGetInt32(out var count) ? count : 0;

        var isFlight = tdxNode.TryGetProperty("flights", out _);
        var isBus = tdxNode.TryGetProperty("buses", out _);
        var itemCount = isFlight
            ? (tdxNode.TryGetProperty("flight_count", out var flightCountNode) && flightCountNode.TryGetInt32(out var fc) ? fc : 0)
            : isBus
                ? (tdxNode.TryGetProperty("bus_count", out var busCountNode) && busCountNode.TryGetInt32(out var bc) ? bc : 0)
                : trainCount;

        var modeLabel = mode switch
        {
            "rail" => "台鐵",
            "hsr" => "高鐵",
            "bus" => "公車",
            "flight" => "航班",
            _ => mode
        };

        var lines = new List<string>
        {
            !string.IsNullOrWhiteSpace(origin) || !string.IsNullOrWhiteSpace(destination)
                ? $"{modeLabel}查詢：{origin} → {destination}（{date}）"
                : $"{modeLabel}查詢：{query}",
            $"共 {itemCount} 筆，來源：{sources}"
        };

        if (!string.IsNullOrWhiteSpace(retrievedAt))
            lines.Add($"查詢時間：{retrievedAt}");

        lines.Add(string.Empty);

        var items = ParseTdxTimetableResult(tdxNode);
        if (items.Count == 0)
        {
            lines.Add("目前沒有取得可用結果。");
            return string.Join('\n', lines);
        }

        foreach (var item in items.Take(15))
        {
            lines.Add($"  {item.Title}  {item.Snippet}");
        }

        if (itemCount > 15)
            lines.Add($"...（還有 {itemCount - 15} 筆）");

        return string.Join('\n', lines);
    }

    private static string FormatBusStopStatus(int stopStatus)
        => stopStatus switch
        {
            0 => "正常進站",
            1 => "尚未發車",
            2 => "交管不停靠",
            3 => "末班已過",
            4 => "今日未營運",
            _ => "狀態未知"
        };
}

public sealed class HighLevelQueryToolResult
{
    public bool Success { get; set; }
    public string Reply { get; set; } = string.Empty;
    public string? Error { get; set; }
    public string ToolId { get; set; } = string.Empty;
    public string Engine { get; set; } = string.Empty;
    public IReadOnlyList<HighLevelQuerySearchResult> Results { get; set; } = Array.Empty<HighLevelQuerySearchResult>();

    public static HighLevelQueryToolResult Fail(string reply, string error)
        => new()
        {
            Success = false,
            Reply = reply,
            Error = error
        };
}

public sealed class HighLevelQuerySearchResult
{
    public int Rank { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
}
