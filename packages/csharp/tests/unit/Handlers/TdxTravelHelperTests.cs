using Broker.Handlers.Travel;

namespace Unit.Tests.Handlers;

/// <summary>
/// 鎖住 TdxTravelHelper 的純解析邏輯（不碰 HTTP / IO）：
///   - ExtractDate        : 相對日（今/明/後/大後天/下週X）、ISO、M月D日/M/D、未指定回 null
///   - ExtractTimeRange   : HH:mm、N點(含AM/PM校正)、時段詞、首/末班、不限時回 null
///   - ExtractTraStations : 台鐵站名 OD 提取（最長匹配優先、同站異寫去重、需兩個不同站）
///   - ExtractThsrStations: 高鐵站名 OD 提取
///   - ExtractStations    : TRA 優先、無 TRA 起站時 fallback 到 THSR
///   - ExtractAirports    : 機場名 OD 提取
///
/// 規則：絕不改 source；只測確定性純邏輯。
/// 相對日期（今/明/後天、下週X）的期望值用與 source 相同的 today 基準計算，避免綁死日曆。
/// QueryTra/Thsr/FlightAsync、FilterAndFormat*、TryParseHour 為 HTTP/IO 或 private，不在此測。
/// </summary>
public class TdxTravelHelperTests
{
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    // ─────────────────────────── ExtractDate ───────────────────────────

    [Fact]
    public void ExtractDate_Today_ReturnsToday()
    {
        TdxTravelHelper.ExtractDate("今天台北到高雄").Should().Be(Today);
    }

    [Fact]
    public void ExtractDate_Tomorrow_ReturnsTodayPlusOne()
    {
        TdxTravelHelper.ExtractDate("明天早上板橋往高雄").Should().Be(Today.AddDays(1));
    }

    [Fact]
    public void ExtractDate_DayAfterTomorrow_ReturnsTodayPlusTwo()
    {
        // 「後天」必須排除「大後天」誤命中
        TdxTravelHelper.ExtractDate("後天去台中").Should().Be(Today.AddDays(2));
    }

    [Fact]
    public void ExtractDate_TwoDaysAfterTomorrow_ReturnsTodayPlusThree()
    {
        TdxTravelHelper.ExtractDate("大後天回台北").Should().Be(Today.AddDays(3));
    }

    [Fact]
    public void ExtractDate_NextMonday_IsWithinNextSevenDaysAndIsMonday()
    {
        var result = TdxTravelHelper.ExtractDate("下週一出發");
        result.Should().NotBeNull();
        result!.Value.DayOfWeek.Should().Be(DayOfWeek.Monday);

        // daysUntilNextWeek = ((target - today + 7) % 7), 若為 0 則設 7 → 範圍恆為 1..7
        var delta = result.Value.DayNumber - Today.DayNumber;
        delta.Should().BeInRange(1, 7);
    }

    [Fact]
    public void ExtractDate_IsoFormat_ParsesExactDate()
    {
        TdxTravelHelper.ExtractDate("2026-04-10 板橋到高雄")
            .Should().Be(new DateOnly(2026, 4, 10));
    }

    [Fact]
    public void ExtractDate_MonthDayChinese_ProducesThatMonthAndDay()
    {
        var result = TdxTravelHelper.ExtractDate("12月25日台北到台南");
        result.Should().NotBeNull();
        result!.Value.Month.Should().Be(12);
        result.Value.Day.Should().Be(25);
    }

    [Fact]
    public void ExtractDate_MonthDaySlash_ProducesThatMonthAndDay()
    {
        var result = TdxTravelHelper.ExtractDate("3/8 出發");
        result.Should().NotBeNull();
        result!.Value.Month.Should().Be(3);
        result.Value.Day.Should().Be(8);
    }

    [Fact]
    public void ExtractDate_InvalidMonthDay_IgnoredReturnsNull()
    {
        // 13 月不合法 → m>12 不進分支；字串其餘無日期關鍵字 → null
        TdxTravelHelper.ExtractDate("13/40 亂寫").Should().BeNull();
    }

    [Fact]
    public void ExtractDate_NoDateKeyword_ReturnsNull()
    {
        TdxTravelHelper.ExtractDate("台北到高雄").Should().BeNull();
    }

    // ───────────────────────── ExtractTimeRange ─────────────────────────

    [Fact]
    public void ExtractTimeRange_ExplicitHourMinute_GivesMinusOnePlusTwoWindow()
    {
        // 18:00 → (max(0,17), min(24,20)) = (17,20)
        TdxTravelHelper.ExtractTimeRange("18:00 出發").Should().Be((17, 20));
    }

    [Fact]
    public void ExtractTimeRange_HourMinuteNearMidnight_ClampsUpperTo24()
    {
        // 23:30 → (22, min(24,25)=24)
        TdxTravelHelper.ExtractTimeRange("23:30").Should().Be((22, 24));
    }

    [Fact]
    public void ExtractTimeRange_HourMinuteEarly_ClampsLowerToZero()
    {
        // 00:15 → (max(0,-1)=0, 2)
        TdxTravelHelper.ExtractTimeRange("00:15").Should().Be((0, 2));
    }

    [Fact]
    public void ExtractTimeRange_AfternoonHour_AddsTwelve()
    {
        // 「下午3點」hourMatch 先命中 → hr=3，含「下午」→ +12=15 → (14,17)
        TdxTravelHelper.ExtractTimeRange("下午3點").Should().Be((14, 17));
    }

    [Fact]
    public void ExtractTimeRange_MorningHour_NoAmPmAdjust()
    {
        // 「早上8點」hourMatch 命中 → hr=8（無下午/晚上不+12）→ (7,10)
        // 注意：此處先命中 N點 分支，不會落到「早上」時段詞
        TdxTravelHelper.ExtractTimeRange("早上8點").Should().Be((7, 10));
    }

    [Fact]
    public void ExtractTimeRange_MorningWord_ReturnsFiveToTwelve()
    {
        TdxTravelHelper.ExtractTimeRange("早上要到台北").Should().Be((5, 12));
    }

    [Fact]
    public void ExtractTimeRange_Noon_ReturnsElevenToFourteen()
    {
        TdxTravelHelper.ExtractTimeRange("中午的車").Should().Be((11, 14));
    }

    [Fact]
    public void ExtractTimeRange_Afternoon_ReturnsTwelveToEighteen()
    {
        TdxTravelHelper.ExtractTimeRange("下午").Should().Be((12, 18));
    }

    [Fact]
    public void ExtractTimeRange_Evening_ReturnsSeventeenToTwentyFour()
    {
        TdxTravelHelper.ExtractTimeRange("晚上回家").Should().Be((17, 24));
    }

    [Fact]
    public void ExtractTimeRange_BeforeDawn_ReturnsZeroToSix()
    {
        TdxTravelHelper.ExtractTimeRange("凌晨出門").Should().Be((0, 6));
    }

    [Fact]
    public void ExtractTimeRange_FirstTrainWord_ReturnsZeroToEight()
    {
        TdxTravelHelper.ExtractTimeRange("最早一班").Should().Be((0, 8));
    }

    [Fact]
    public void ExtractTimeRange_LastTrainWord_ReturnsTwentyToTwentyFour()
    {
        TdxTravelHelper.ExtractTimeRange("末班車幾點").Should().Be((20, 24));
    }

    [Fact]
    public void ExtractTimeRange_NoTimeKeyword_ReturnsNull()
    {
        TdxTravelHelper.ExtractTimeRange("台北到高雄").Should().BeNull();
    }

    // ───────────────────────── ExtractTraStations ─────────────────────────

    [Fact]
    public void ExtractTraStations_OriginThenDestination_ByAppearanceOrder()
    {
        var (origin, dest) = TdxTravelHelper.ExtractTraStations("板橋往高雄自強號");
        origin.Should().Be("板橋");
        dest.Should().Be("高雄");
    }

    [Fact]
    public void ExtractTraStations_ArrowFormat_Works()
    {
        var (origin, dest) = TdxTravelHelper.ExtractTraStations("南港→台南");
        origin.Should().Be("南港");
        dest.Should().Be("台南");
    }

    [Fact]
    public void ExtractTraStations_LongerNamePreferred_NewZuoyingNotZuoying()
    {
        // 「新左營」(4340) 與「左營」(4350) 不同 ID；最長匹配優先取「新左營」
        var (origin, dest) = TdxTravelHelper.ExtractTraStations("台北到新左營");
        origin.Should().Be("台北");
        dest.Should().Be("新左營");
    }

    [Fact]
    public void ExtractTraStations_SameStationDifferentWriting_DedupedReturnsNull()
    {
        // 「台北」與「臺北」同 ID(1000)，DistinctBy(id) 後只剩一站 → 不足兩站 → (null,null)
        var (origin, dest) = TdxTravelHelper.ExtractTraStations("台北轉臺北");
        origin.Should().BeNull();
        dest.Should().BeNull();
    }

    [Fact]
    public void ExtractTraStations_OnlyOneStation_ReturnsNull()
    {
        var (origin, dest) = TdxTravelHelper.ExtractTraStations("我要去高雄");
        origin.Should().BeNull();
        dest.Should().BeNull();
    }

    [Fact]
    public void ExtractTraStations_NoStation_ReturnsNull()
    {
        var (origin, dest) = TdxTravelHelper.ExtractTraStations("今天天氣很好");
        origin.Should().BeNull();
        dest.Should().BeNull();
    }

    // ───────────────────────── ExtractThsrStations ─────────────────────────

    [Fact]
    public void ExtractThsrStations_BasicOd_Works()
    {
        var (origin, dest) = TdxTravelHelper.ExtractThsrStations("台北到台中高鐵");
        origin.Should().Be("台北");
        dest.Should().Be("台中");
    }

    // ─────────────────────────── ExtractStations ───────────────────────────

    [Fact]
    public void ExtractStations_PrefersTraWhenTraOriginFound()
    {
        // 「員林」只在台鐵表 → TRA 分支命中、回傳台鐵結果
        var (origin, dest) = TdxTravelHelper.ExtractStations("員林到台南");
        origin.Should().Be("員林");
        dest.Should().Be("台南");
    }

    [Fact]
    public void ExtractStations_FallsBackToThsrWhenTraOriginNull()
    {
        // 「雲林」只存在於高鐵表（台鐵表沒有）→ TRA 起站為 null → fallback THSR
        var (origin, dest) = TdxTravelHelper.ExtractStations("雲林到台北");
        origin.Should().Be("雲林");
        dest.Should().Be("台北");
    }

    [Fact]
    public void ExtractStations_NoMatchAnywhere_ReturnsNull()
    {
        var (origin, dest) = TdxTravelHelper.ExtractStations("隨便走走");
        origin.Should().BeNull();
        dest.Should().BeNull();
    }

    // ─────────────────────────── ExtractAirports ───────────────────────────

    [Fact]
    public void ExtractAirports_TwoDistinctAirports_ByOrder()
    {
        // 松山(TSA) → 馬公(MZG)，不同 IATA code
        var (origin, dest) = TdxTravelHelper.ExtractAirports("松山飛馬公");
        origin.Should().Be("松山");
        dest.Should().Be("馬公");
    }

    [Fact]
    public void ExtractAirports_SameIataDifferentNames_DedupedReturnsNull()
    {
        // 「高雄」「小港」「高雄小港」全為 KHH → DistinctBy(id) 後僅一個 → 不足兩 → null
        var (origin, dest) = TdxTravelHelper.ExtractAirports("高雄小港機場");
        origin.Should().BeNull();
        dest.Should().BeNull();
    }

    [Fact]
    public void ExtractAirports_NoAirport_ReturnsNull()
    {
        var (origin, dest) = TdxTravelHelper.ExtractAirports("我想出國玩");
        origin.Should().BeNull();
        dest.Should().BeNull();
    }
}
