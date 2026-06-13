using System.Text.Json;
using Broker.Handlers.Travel;
using BrokerCore.Contracts;

namespace Unit.Tests.Handlers;

/// <summary>
/// 鎖住 TravelTdxResponseHelper 對 TDX 回應的「信封塑形」契約：
///   CreateSuccess → ExecutionResult.Ok 包 { mode, query, retrieved_at, tdx=payload, sources_used=[label] }
///   CreateEmpty   → 同信封、但 tdx 依 mode 切成 bus/flight/train 的空 payload（*_count=0、空陣列）
///
/// retrieved_at（UtcNow）與 date（Today）是非確定性時間欄位 —— 不鎖值，只驗其存在/可解析；
/// 其餘鍵名、結構、passthrough、mode switch 全為純邏輯、確定性可鎖。
/// </summary>
public class TravelTdxResponseHelperTests
{
    private static JsonElement Parse(ExecutionResult er)
    {
        er.ResultPayload.Should().NotBeNull();
        return JsonDocument.Parse(er.ResultPayload!).RootElement;
    }

    // ---- CreateSuccess ----

    [Fact]
    public void CreateSuccess_ProducesOkResult_WithRequestId()
    {
        var er = TravelTdxResponseHelper.CreateSuccess(
            "req_1", "train", "台北到台中", new { foo = "bar" }, "TDX");

        er.Success.Should().BeTrue();
        er.RequestId.Should().Be("req_1");
        er.ErrorMessage.Should().BeNull();
        er.ResultPayload.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void CreateSuccess_EnvelopeHasExpectedKeys()
    {
        var er = TravelTdxResponseHelper.CreateSuccess(
            "req_2", "train", "高雄到台南", new { count = 3 }, "TDX-Rail");

        var root = Parse(er);
        root.GetProperty("mode").GetString().Should().Be("train");
        root.GetProperty("query").GetString().Should().Be("高雄到台南");
        root.TryGetProperty("retrieved_at", out _).Should().BeTrue();
        root.TryGetProperty("tdx", out _).Should().BeTrue();
        root.TryGetProperty("sources_used", out _).Should().BeTrue();
    }

    [Fact]
    public void CreateSuccess_RetrievedAt_IsRoundTripParsableTimestamp()
    {
        var er = TravelTdxResponseHelper.CreateSuccess(
            "req_3", "bus", "q", new { }, "TDX");

        var root = Parse(er);
        var ts = root.GetProperty("retrieved_at").GetString();
        DateTimeOffset.TryParse(ts, out _).Should().BeTrue("retrieved_at 用 ISO-8601 \"O\" 格式輸出");
    }

    [Fact]
    public void CreateSuccess_PayloadIsPassedThroughVerbatimUnderTdx()
    {
        var payload = new { origin = "TPE", destination = "KHH", flights = new[] { "CI", "BR" } };
        var er = TravelTdxResponseHelper.CreateSuccess(
            "req_4", "flight", "TPE-KHH", payload, "TDX-Air");

        var tdx = Parse(er).GetProperty("tdx");
        tdx.GetProperty("origin").GetString().Should().Be("TPE");
        tdx.GetProperty("destination").GetString().Should().Be("KHH");
        tdx.GetProperty("flights").GetArrayLength().Should().Be(2);
        tdx.GetProperty("flights")[0].GetString().Should().Be("CI");
        tdx.GetProperty("flights")[1].GetString().Should().Be("BR");
    }

    [Fact]
    public void CreateSuccess_SourcesUsed_IsSingleElementArrayOfLabel()
    {
        var er = TravelTdxResponseHelper.CreateSuccess(
            "req_5", "train", "q", new { }, "TDX-Source-Label");

        var sources = Parse(er).GetProperty("sources_used");
        sources.ValueKind.Should().Be(JsonValueKind.Array);
        sources.GetArrayLength().Should().Be(1);
        sources[0].GetString().Should().Be("TDX-Source-Label");
    }

    [Fact]
    public void CreateSuccess_EmptyObjectPayload_StillWrappedUnderTdx()
    {
        // 邊界：payload 為空匿名物件 → tdx 仍是物件、無多餘鍵
        var er = TravelTdxResponseHelper.CreateSuccess(
            "req_6", "train", "q", new { }, "TDX");

        var tdx = Parse(er).GetProperty("tdx");
        tdx.ValueKind.Should().Be(JsonValueKind.Object);
        tdx.EnumerateObject().Should().BeEmpty();
    }

    // ---- CreateEmpty: 共同信封 ----

    [Fact]
    public void CreateEmpty_ProducesOkResult_WithEnvelopeKeys()
    {
        var er = TravelTdxResponseHelper.CreateEmpty("req_7", "train", "台北到花蓮", "TDX");

        er.Success.Should().BeTrue();
        er.RequestId.Should().Be("req_7");

        var root = Parse(er);
        root.GetProperty("mode").GetString().Should().Be("train");
        root.GetProperty("query").GetString().Should().Be("台北到花蓮");
        root.TryGetProperty("retrieved_at", out _).Should().BeTrue();
        root.GetProperty("sources_used")[0].GetString().Should().Be("TDX");
    }

    [Fact]
    public void CreateEmpty_CommonPayloadFields_ArePresentAndBlank()
    {
        var er = TravelTdxResponseHelper.CreateEmpty("req_8", "train", "q", "TDX");

        var tdx = Parse(er).GetProperty("tdx");
        tdx.GetProperty("source").GetString().Should().Be("TDX");
        tdx.GetProperty("origin").GetString().Should().BeEmpty();
        tdx.GetProperty("destination").GetString().Should().BeEmpty();
        // date 為 Today（非確定性）—— 只驗存在且符合 yyyy-MM-dd 形狀
        var date = tdx.GetProperty("date").GetString();
        DateTime.TryParseExact(date, "yyyy-MM-dd", null,
            System.Globalization.DateTimeStyles.None, out _).Should().BeTrue();
    }

    // ---- CreateEmpty: mode switch ----

    [Fact]
    public void CreateEmpty_BusMode_HasZeroBusCountAndEmptyBuses()
    {
        var er = TravelTdxResponseHelper.CreateEmpty("req_9", "bus", "q", "TDX-Bus");

        var tdx = Parse(er).GetProperty("tdx");
        tdx.GetProperty("bus_count").GetInt32().Should().Be(0);
        tdx.GetProperty("buses").GetArrayLength().Should().Be(0);
        // bus 分支不應帶 train/flight 的計數鍵
        tdx.TryGetProperty("train_count", out _).Should().BeFalse();
        tdx.TryGetProperty("flight_count", out _).Should().BeFalse();
    }

    [Fact]
    public void CreateEmpty_FlightMode_HasZeroFlightCountAndEmptyFlights()
    {
        var er = TravelTdxResponseHelper.CreateEmpty("req_10", "flight", "q", "TDX-Air");

        var tdx = Parse(er).GetProperty("tdx");
        tdx.GetProperty("flight_count").GetInt32().Should().Be(0);
        tdx.GetProperty("flights").GetArrayLength().Should().Be(0);
        tdx.TryGetProperty("bus_count", out _).Should().BeFalse();
        tdx.TryGetProperty("train_count", out _).Should().BeFalse();
    }

    [Fact]
    public void CreateEmpty_TrainMode_HasZeroTrainCountAndEmptyTrains()
    {
        var er = TravelTdxResponseHelper.CreateEmpty("req_11", "train", "q", "TDX-Rail");

        var tdx = Parse(er).GetProperty("tdx");
        tdx.GetProperty("train_count").GetInt32().Should().Be(0);
        tdx.GetProperty("trains").GetArrayLength().Should().Be(0);
        tdx.TryGetProperty("bus_count", out _).Should().BeFalse();
        tdx.TryGetProperty("flight_count", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("rail")]
    public void CreateEmpty_UnknownMode_FallsBackToTrainShape(string mode)
    {
        // 邊界：default 分支 —— 任何非 bus/flight 的 mode 都塑成 train 形狀
        var er = TravelTdxResponseHelper.CreateEmpty("req_12", mode, "q", "TDX");

        var tdx = Parse(er).GetProperty("tdx");
        tdx.TryGetProperty("train_count", out var tc).Should().BeTrue();
        tc.GetInt32().Should().Be(0);
        tdx.GetProperty("trains").GetArrayLength().Should().Be(0);
        // mode 信封仍透傳原值（即使是空字串/未知）
        Parse(er).GetProperty("mode").GetString().Should().Be(mode);
    }
}
