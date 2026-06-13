using Broker.Handlers.Travel;

namespace Unit.Tests.Handlers;

/// <summary>
/// 鎖住 TdxBusTravelHelper 唯一的確定性純解析方法 ExtractCityAndRoute(query)：
///   - 縣市別名比對（最長別名優先、台/臺 異體字、OrdinalIgnoreCase）
///   - 比對到縣市後，自剩餘字串抽路線名（去標點 / 去查詢關鍵字 / RouteTokenRegex）
///   - 無縣市時 city=null、route 退而用全字串抽取
///   - 數字 / 含字母 / 顏色幹線 等路線 token
///   - 空白 / 純關鍵字 等邊界
///
/// 規則：絕不改 source；只測 public 確定性純邏輯。
/// QueryBusAsync 走 TdxApiService（HTTP/IO）、FilterAndFormatBusEstimatedTime /
/// GetLocalizedText / FormatDirection / ExtractRouteName 為 private，皆不在此測。
/// 期望值以閱讀 source（CityAliases / ExtractRouteName / RouteTokenRegex）逐字推導，非執行產出。
/// </summary>
public class TdxBusTravelHelperTests
{
    // ─────────────── 縣市 + 路線：正常 ───────────────

    [Fact]
    public void ExtractCityAndRoute_CityWithKeywordAndNumericRoute_StripsKeyword()
    {
        // "台北市" 命中別名 → canonical "臺北市"；剩餘 "公車 307" 去掉關鍵字「公車」→ "307"
        var (city, route) = TdxBusTravelHelper.ExtractCityAndRoute("台北市 公車 307");
        city.Should().Be("臺北市");
        route.Should().Be("307");
    }

    [Fact]
    public void ExtractCityAndRoute_NormalizesTaiwaneseVariantToCanonical()
    {
        // 別名 "台北" 命中 → canonical 一律回正體 "臺北市"
        var (city, route) = TdxBusTravelHelper.ExtractCityAndRoute("台北307");
        city.Should().Be("臺北市");
        route.Should().Be("307");
    }

    [Fact]
    public void ExtractCityAndRoute_NewTaipeiCity_NumericRoute()
    {
        var (city, route) = TdxBusTravelHelper.ExtractCityAndRoute("新北市 936");
        city.Should().Be("新北市");
        route.Should().Be("936");
    }

    [Fact]
    public void ExtractCityAndRoute_AlphaNumericRoutePreserved()
    {
        // RouteTokenRegex 第一支 [0-9A-Za-z]+... 應原樣保留含字母路線
        var (city, route) = TdxBusTravelHelper.ExtractCityAndRoute("台北市 671A");
        city.Should().Be("臺北市");
        route.Should().Be("671A");
    }

    [Fact]
    public void ExtractCityAndRoute_ColorTrunkRouteMatched()
    {
        // RouteTokenRegex CJK 分支：[紅...]+(?:線|路)? 命中 "紅線"
        var (city, route) = TdxBusTravelHelper.ExtractCityAndRoute("台中市 紅線");
        city.Should().Be("臺中市");
        route.Should().Be("紅線");
    }

    [Fact]
    public void ExtractCityAndRoute_DropsQueryVerbKeyword()
    {
        // 關鍵字「查詢」「到站」應被剔除，留下路線 "8"
        var (city, route) = TdxBusTravelHelper.ExtractCityAndRoute("高雄市 查詢 8 到站");
        city.Should().Be("高雄市");
        route.Should().Be("8");
    }

    [Fact]
    public void ExtractCityAndRoute_CollapsesWhitespaceBeforeParsing()
    {
        // 多重空白先被正規化為單一空白，不影響縣市/路線抽取
        var (city, route) = TdxBusTravelHelper.ExtractCityAndRoute("桃園    公車   1");
        city.Should().Be("桃園市");
        route.Should().Be("1");
    }

    // ─────────────── 無縣市：fallback ───────────────

    [Fact]
    public void ExtractCityAndRoute_NoCity_RouteFromWholeString()
    {
        // 無任何縣市別名 → city=null；route 退而對整串抽取（去關鍵字「公車」）
        var (city, route) = TdxBusTravelHelper.ExtractCityAndRoute("307公車");
        city.Should().BeNull();
        route.Should().Be("307");
    }

    [Fact]
    public void ExtractCityAndRoute_CityOnly_ShorterAliasLeavesSuffixAsRoute()
    {
        // 別名以長度由大到小嘗試："高雄市"(3) 命中 → 剩餘空 → 該分支不回傳；
        // 接著同城較短別名 "高雄"(2) 命中 → 剩餘 "市" → ExtractRouteName fallback 取 "市"。
        // 鎖住：縣市仍正規化為 "高雄市"，且短別名會把後綴字當路線（已知行為）。
        var (city, route) = TdxBusTravelHelper.ExtractCityAndRoute("高雄市");
        city.Should().Be("高雄市");
        route.Should().Be("市");
    }

    // ─────────────── 邊界：null / 空白 / 純關鍵字 ───────────────

    [Fact]
    public void ExtractCityAndRoute_Null_ReturnsNullNull()
    {
        var (city, route) = TdxBusTravelHelper.ExtractCityAndRoute(null!);
        city.Should().BeNull();
        route.Should().BeNull();
    }

    [Fact]
    public void ExtractCityAndRoute_Whitespace_ReturnsNullNull()
    {
        var (city, route) = TdxBusTravelHelper.ExtractCityAndRoute("   ");
        city.Should().BeNull();
        route.Should().BeNull();
    }

    [Fact]
    public void ExtractCityAndRoute_OnlyKeyword_NoRoute_ReturnsNullNull()
    {
        // 無縣市，剩 "公車" 全是關鍵字被剔除 → ExtractRouteName 回 null → (null, null)
        var (city, route) = TdxBusTravelHelper.ExtractCityAndRoute("公車");
        city.Should().BeNull();
        route.Should().BeNull();
    }
}
