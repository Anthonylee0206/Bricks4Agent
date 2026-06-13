using TransportTdxWorker.Services;

namespace Unit.Tests.Transport;

public class TransportQueryContextResolverTests
{
    private static TransportQueryContextResolver NewResolver() => new();

    // ---- NormalizeCityToTdx ----

    [Theory]
    [InlineData("臺北市", "Taipei")]
    [InlineData("台北", "Taipei")]
    [InlineData("新北", "NewTaipei")]
    [InlineData("桃園市", "Taoyuan")]
    [InlineData("台中市", "Taichung")]
    [InlineData("臺南", "Tainan")]
    [InlineData("高雄", "Kaohsiung")]
    public void NormalizeCityToTdx_maps_known_aliases(string city, string expected)
    {
        NewResolver().NormalizeCityToTdx(city).Should().Be(expected);
    }

    [Fact]
    public void NormalizeCityToTdx_matches_alias_embedded_in_longer_text()
    {
        // Aliases are matched via Contains, so surrounding text still resolves.
        NewResolver().NormalizeCityToTdx("我想去高雄玩").Should().Be("Kaohsiung");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeCityToTdx_returns_null_for_empty_or_whitespace(string? city)
    {
        NewResolver().NormalizeCityToTdx(city).Should().BeNull();
    }

    [Fact]
    public void NormalizeCityToTdx_returns_null_for_unknown_city()
    {
        NewResolver().NormalizeCityToTdx("基隆市").Should().BeNull();
    }

    // ---- Station id lookups (TRA / THSR / Airport) ----

    [Fact]
    public void GetTraStationId_returns_exact_match()
    {
        NewResolver().GetTraStationId("板橋").Should().Be("1020");
    }

    [Fact]
    public void GetTraStationId_handles_simplified_and_traditional_taipei()
    {
        var resolver = NewResolver();
        resolver.GetTraStationId("臺北").Should().Be("1000");
        resolver.GetTraStationId("台北").Should().Be("1000");
    }

    [Fact]
    public void GetTraStationId_falls_back_to_substring_match()
    {
        // No exact key "新左營站", but contains "新左營".
        NewResolver().GetTraStationId("新左營站").Should().Be("4340");
    }

    [Fact]
    public void GetTraStationId_returns_null_for_unknown_station()
    {
        NewResolver().GetTraStationId("不存在的站").Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void GetTraStationId_returns_null_for_empty_input(string? name)
    {
        NewResolver().GetTraStationId(name).Should().BeNull();
    }

    [Fact]
    public void GetThsrStationId_differs_from_tra_for_same_station()
    {
        // 板橋 is 1020 on TRA but 1010 on THSR — confirms separate maps.
        var resolver = NewResolver();
        resolver.GetThsrStationId("板橋").Should().Be("1010");
        resolver.GetThsrStationId("高雄").Should().Be("1070");
    }

    [Fact]
    public void GetAirportCode_returns_iata_code()
    {
        var resolver = NewResolver();
        resolver.GetAirportCode("桃園").Should().Be("TPE");
        resolver.GetAirportCode("松山").Should().Be("TSA");
        resolver.GetAirportCode("臺中").Should().Be("RMQ");
    }

    [Fact]
    public void GetAirportCode_returns_null_for_unknown_airport()
    {
        NewResolver().GetAirportCode("倫敦").Should().BeNull();
    }

    // ---- ParseDate ----

    [Fact]
    public void ParseDate_parses_iso_date()
    {
        NewResolver().ParseDate("2026-07-15").Should().Be(new DateOnly(2026, 7, 15));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void ParseDate_returns_null_for_unparsable(string? text)
    {
        NewResolver().ParseDate(text).Should().BeNull();
    }

    // ---- ParseTimeRange ----

    [Theory]
    [InlineData("morning", 5, 12)]
    [InlineData("afternoon", 12, 18)]
    [InlineData("evening", 18, 24)]
    [InlineData("night", 0, 6)]
    public void ParseTimeRange_maps_known_ranges(string range, int start, int end)
    {
        NewResolver().ParseTimeRange(range).Should().Be((start, end));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dawn")]
    [InlineData("Morning")] // case-sensitive switch — capitalized is unknown
    public void ParseTimeRange_returns_null_for_unknown(string? range)
    {
        NewResolver().ParseTimeRange(range).Should().BeNull();
    }

    // ---- Resolve ----

    [Fact]
    public void Resolve_preserves_existing_context_values()
    {
        var context = new Dictionary<string, string?>
        {
            ["origin"] = "板橋",
            ["destination"] = "高雄"
        };

        var resolved = NewResolver().Resolve("rail", "板橋到高雄的火車", context);

        // Both already present, so station extraction must not overwrite.
        resolved["origin"].Should().Be("板橋");
        resolved["destination"].Should().Be("高雄");
    }

    [Fact]
    public void Resolve_extracts_rail_stations_in_query_order()
    {
        var resolved = NewResolver().Resolve(
            "rail",
            "我要從板橋搭車到高雄",
            new Dictionary<string, string?>());

        resolved["origin"].Should().Be("板橋");
        resolved["destination"].Should().Be("高雄");
    }

    [Fact]
    public void Resolve_leaves_stations_unset_when_fewer_than_two_found()
    {
        var resolved = NewResolver().Resolve(
            "rail",
            "從板橋出發",
            new Dictionary<string, string?>());

        // ExtractOrderedStations returns (null, null) when <2 distinct stations.
        resolved["origin"].Should().BeNull();
        resolved["destination"].Should().BeNull();
    }

    [Fact]
    public void Resolve_extracts_iso_date_into_context()
    {
        var resolved = NewResolver().Resolve(
            "rail",
            "2026-07-15 板橋到高雄",
            new Dictionary<string, string?>());

        resolved["date"].Should().Be("2026-07-15");
    }

    [Fact]
    public void Resolve_does_not_override_existing_date()
    {
        var resolved = NewResolver().Resolve(
            "rail",
            "2026-07-15 板橋到高雄",
            new Dictionary<string, string?> { ["date"] = "2026-01-01" });

        resolved["date"].Should().Be("2026-01-01");
    }

    [Theory]
    [InlineData("上午的車", "morning")]
    [InlineData("早上出發", "morning")]
    [InlineData("下午到", "afternoon")]
    [InlineData("晚上的班次", "evening")]
    [InlineData("凌晨班車", "night")]
    public void Resolve_extracts_time_range_from_query(string query, string expected)
    {
        var resolved = NewResolver().Resolve("rail", query, new Dictionary<string, string?>());
        resolved["time_range"].Should().Be(expected);
    }

    [Fact]
    public void Resolve_does_not_set_time_range_when_no_keyword()
    {
        var resolved = NewResolver().Resolve("rail", "板橋到高雄", new Dictionary<string, string?>());
        resolved.ContainsKey("time_range").Should().BeFalse();
    }

    [Fact]
    public void Resolve_bus_extracts_city_and_numeric_route()
    {
        var resolved = NewResolver().Resolve(
            "bus",
            "台北市的307公車",
            new Dictionary<string, string?>());

        resolved["city"].Should().Be("臺北市");
        resolved["route"].Should().Be("307");
    }

    [Fact]
    public void Resolve_bus_extracts_colored_line_route()
    {
        var resolved = NewResolver().Resolve(
            "bus",
            "高雄的紅12路線",
            new Dictionary<string, string?>());

        resolved["city"].Should().Be("高雄市");
        resolved["route"].Should().Be("紅12");
    }

    [Fact]
    public void Resolve_bus_does_not_override_existing_route()
    {
        var resolved = NewResolver().Resolve(
            "bus",
            "台北市的307公車",
            new Dictionary<string, string?> { ["route"] = "綠1" });

        resolved["route"].Should().Be("綠1");
    }

    [Fact]
    public void Resolve_flight_uses_airport_codes_as_stations()
    {
        var resolved = NewResolver().Resolve(
            "flight",
            "從松山飛高雄",
            new Dictionary<string, string?>());

        // ApplyStations on flight mode stores airport names (keys), not codes.
        resolved["origin"].Should().Be("松山");
        resolved["destination"].Should().Be("高雄");
    }

    [Fact]
    public void Resolve_unknown_mode_only_applies_generic_date_and_time()
    {
        var resolved = NewResolver().Resolve(
            "unknown",
            "2026-07-15 上午 板橋到高雄",
            new Dictionary<string, string?>());

        resolved["date"].Should().Be("2026-07-15");
        resolved["time_range"].Should().Be("morning");
        // No station/city/route resolution for an unrecognized mode.
        resolved.ContainsKey("origin").Should().BeFalse();
        resolved.ContainsKey("destination").Should().BeFalse();
        resolved.ContainsKey("city").Should().BeFalse();
    }

    [Fact]
    public void Resolve_returns_case_insensitive_dictionary()
    {
        var resolved = NewResolver().Resolve(
            "rail",
            "板橋到高雄",
            new Dictionary<string, string?> { ["Origin"] = "板橋", ["Destination"] = "高雄" });

        // Resolved dict is OrdinalIgnoreCase, so lowercase access hits the seeded values.
        resolved["origin"].Should().Be("板橋");
        resolved["destination"].Should().Be("高雄");
    }
}
