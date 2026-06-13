using System.Text.Json;
using Broker.Helpers;

namespace Unit.Tests.Helpers;

/// <summary>
/// 鎖住 PayloadHelper 五個確定性純邏輯方法的解析契約：
///   - GetArgsElement：args / tool_args 包裝層的優先序與 fallback
///   - IsPayloadRouteValid：route / tool_name 比對(大小寫不敏感、缺欄位視為通過)
///   - TryGetString / TryGetInt：多鍵名抽取、型別容錯(string number 互轉)
///   - ResolveSandboxedPath：沙箱範圍守門(traversal 擋掉、合法路徑放行)
///
/// 這些是 ApprovedRequest payload 解析共用工具、被 dispatch 路徑大量依賴，
/// 鍵名優先序 / 型別容錯 / 沙箱邊界一旦回退就是真錢路由風險。
/// </summary>
public class PayloadHelperTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---- GetArgsElement ----

    [Fact]
    public void GetArgsElement_PrefersArgsObject()
    {
        var root = Parse("""{ "args": { "symbol": "BTC" }, "tool_args": { "symbol": "ETH" } }""");
        var args = PayloadHelper.GetArgsElement(root);
        args.GetProperty("symbol").GetString().Should().Be("BTC");
    }

    [Fact]
    public void GetArgsElement_FallsBackToToolArgs_WhenNoArgs()
    {
        var root = Parse("""{ "tool_args": { "symbol": "ETH" } }""");
        var args = PayloadHelper.GetArgsElement(root);
        args.GetProperty("symbol").GetString().Should().Be("ETH");
    }

    [Fact]
    public void GetArgsElement_ReturnsRoot_WhenNoWrapper()
    {
        var root = Parse("""{ "symbol": "SOL" }""");
        var args = PayloadHelper.GetArgsElement(root);
        args.GetProperty("symbol").GetString().Should().Be("SOL");
    }

    [Fact]
    public void GetArgsElement_IgnoresArgs_WhenNotObject()
    {
        // args 是字串而非物件 → 不該被當包裝層、應 fallback 到 root
        var root = Parse("""{ "args": "not-an-object", "symbol": "XRP" }""");
        var args = PayloadHelper.GetArgsElement(root);
        args.GetProperty("symbol").GetString().Should().Be("XRP");
    }

    [Fact]
    public void GetArgsElement_IgnoresArgsArray_FallsBackToToolArgs()
    {
        // args 是陣列(非 Object) → 跳過、改用 tool_args
        var root = Parse("""{ "args": [1, 2, 3], "tool_args": { "k": "v" } }""");
        var args = PayloadHelper.GetArgsElement(root);
        args.GetProperty("k").GetString().Should().Be("v");
    }

    // ---- IsPayloadRouteValid ----

    [Fact]
    public void IsPayloadRouteValid_MatchingRoute_IsValid()
    {
        var root = Parse("""{ "route": "trade.perp.open" }""");
        PayloadHelper.IsPayloadRouteValid(root, "trade.perp.open").Should().BeTrue();
    }

    [Fact]
    public void IsPayloadRouteValid_CaseInsensitiveMatch_IsValid()
    {
        var root = Parse("""{ "route": "Trade.Perp.Open" }""");
        PayloadHelper.IsPayloadRouteValid(root, "trade.perp.open").Should().BeTrue();
    }

    [Fact]
    public void IsPayloadRouteValid_UsesToolNameWhenRouteAbsent()
    {
        var root = Parse("""{ "tool_name": "trade.perp.open" }""");
        PayloadHelper.IsPayloadRouteValid(root, "trade.perp.open").Should().BeTrue();
    }

    [Fact]
    public void IsPayloadRouteValid_MismatchedRoute_IsInvalid()
    {
        var root = Parse("""{ "route": "trade.spot.buy" }""");
        PayloadHelper.IsPayloadRouteValid(root, "trade.perp.open").Should().BeFalse();
    }

    [Fact]
    public void IsPayloadRouteValid_MissingRoute_IsValid()
    {
        // 缺 route / tool_name → 視為不衝突、通過(寬鬆)
        var root = Parse("""{ "symbol": "BTC" }""");
        PayloadHelper.IsPayloadRouteValid(root, "trade.perp.open").Should().BeTrue();
    }

    [Fact]
    public void IsPayloadRouteValid_EmptyRouteString_IsValid()
    {
        var root = Parse("""{ "route": "" }""");
        PayloadHelper.IsPayloadRouteValid(root, "trade.perp.open").Should().BeTrue();
    }

    // ---- TryGetString ----

    [Fact]
    public void TryGetString_FindsFirstMatchingKey()
    {
        var el = Parse("""{ "symbol": "BTC", "sym": "ETH" }""");
        PayloadHelper.TryGetString(el, "symbol", "sym").Should().Be("BTC");
    }

    [Fact]
    public void TryGetString_FallsThroughToSecondKey()
    {
        var el = Parse("""{ "sym": "ETH" }""");
        PayloadHelper.TryGetString(el, "symbol", "sym").Should().Be("ETH");
    }

    [Fact]
    public void TryGetString_ReturnsNull_WhenNoKeyMatches()
    {
        var el = Parse("""{ "other": "x" }""");
        PayloadHelper.TryGetString(el, "symbol", "sym").Should().BeNull();
    }

    [Fact]
    public void TryGetString_ReturnsNull_WhenValueIsNotString()
    {
        // symbol 是數字 → 不該回傳、應 null(型別嚴格)
        var el = Parse("""{ "symbol": 123 }""");
        PayloadHelper.TryGetString(el, "symbol").Should().BeNull();
    }

    [Fact]
    public void TryGetString_SkipsNonStringKey_FindsLaterStringKey()
    {
        var el = Parse("""{ "symbol": 123, "sym": "ETH" }""");
        PayloadHelper.TryGetString(el, "symbol", "sym").Should().Be("ETH");
    }

    // ---- TryGetInt ----

    [Fact]
    public void TryGetInt_ParsesNumber()
    {
        var el = Parse("""{ "leverage": 5 }""");
        PayloadHelper.TryGetInt(el, "leverage").Should().Be(5);
    }

    [Fact]
    public void TryGetInt_ParsesNumericString()
    {
        // 字串型的數字也要容錯轉換(payload 來源可能 stringify)
        var el = Parse("""{ "leverage": "10" }""");
        PayloadHelper.TryGetInt(el, "leverage").Should().Be(10);
    }

    [Fact]
    public void TryGetInt_FallsThroughToSecondKey()
    {
        var el = Parse("""{ "lev": 3 }""");
        PayloadHelper.TryGetInt(el, "leverage", "lev").Should().Be(3);
    }

    [Fact]
    public void TryGetInt_ReturnsNull_WhenNoKeyMatches()
    {
        var el = Parse("""{ "other": 1 }""");
        PayloadHelper.TryGetInt(el, "leverage").Should().BeNull();
    }

    [Fact]
    public void TryGetInt_ReturnsNull_ForNonNumericString()
    {
        var el = Parse("""{ "leverage": "abc" }""");
        PayloadHelper.TryGetInt(el, "leverage").Should().BeNull();
    }

    [Fact]
    public void TryGetInt_ReturnsNull_ForBoolValue()
    {
        // bool 既非 Number 也非可解析 String → null
        var el = Parse("""{ "leverage": true }""");
        PayloadHelper.TryGetInt(el, "leverage").Should().BeNull();
    }

    [Fact]
    public void TryGetInt_HandlesNegativeNumber()
    {
        var el = Parse("""{ "delta": -7 }""");
        PayloadHelper.TryGetInt(el, "delta").Should().Be(-7);
    }

    [Fact]
    public void TryGetInt_SkipsNonParsableKey_FindsLaterValidKey()
    {
        var el = Parse("""{ "leverage": "abc", "lev": 4 }""");
        PayloadHelper.TryGetInt(el, "leverage", "lev").Should().Be(4);
    }

    // ---- ResolveSandboxedPath ----
    // sandboxRoot 用各平台 temp 目錄、確定性且跨平台。

    private static string SandboxRoot()
        => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "payload_helper_sandbox"));

    [Fact]
    public void ResolveSandboxedPath_LegalRelativePath_StaysInside()
    {
        var root = SandboxRoot();
        var resolved = PayloadHelper.ResolveSandboxedPath(root, "sub/file.txt");
        resolved.Should().NotBeNull();
        resolved!.StartsWith(root, StringComparison.OrdinalIgnoreCase).Should().BeTrue();
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("../../etc/passwd")]
    [InlineData("sub/../../escape.txt")]
    public void ResolveSandboxedPath_Traversal_ReturnsNull(string maliciousPath)
    {
        var root = SandboxRoot();
        PayloadHelper.ResolveSandboxedPath(root, maliciousPath).Should().BeNull();
    }

    [Fact]
    public void ResolveSandboxedPath_EmptyPath_StaysInsideRoot()
    {
        // 空路徑 → 解析回 root 本身、仍在沙箱內
        var root = SandboxRoot();
        var resolved = PayloadHelper.ResolveSandboxedPath(root, "");
        resolved.Should().NotBeNull();
        resolved!.StartsWith(root, StringComparison.OrdinalIgnoreCase).Should().BeTrue();
    }
}
