using Broker.Handlers.Travel;
using BrokerCore.Contracts;
using Microsoft.Extensions.Logging;

namespace Unit.Tests.Handlers;

/// <summary>
/// 鎖住 TravelSearchHelper.ExecuteTravelSearchAsync 的「query 缺失 guard」契約。
///
/// 該 method 唯一不觸網（DuckDuckGo / HTTP fetch）的確定性純邏輯路徑是：
///   payload 解析出的 query 為 null / 空 / 純空白 時 → 立即回 ExecutionResult.Fail("query is required.")。
///
/// 這條 guard 之所以重要：它必須在任何外部 IO（SearchDuckDuckGoAsync、SharedHttpClient.GetStringAsync）
/// 之前短路。我們用「會拋例外的 queryDecorator」+「零互動 logger」反證早退時根本沒走進 try 區塊，
/// 因此這些測試完全不需要網路。
///
/// 不測搜尋成功路徑：那段純粹是 WebSearchHelper 的網路 IO 與 HTTP fetch，無法在單元測試中確定性重現。
/// </summary>
public class TravelSearchHelperTests
{
    // payload 解析委派給 PayloadHelper：args 物件取自 "args" 或 legacy "tool_args"，否則 root 本身。
    private static ApprovedRequest Req(string payload, string requestId = "req_travel_1")
        => new() { RequestId = requestId, Payload = payload };

    // 早退路徑「絕不該」呼叫的 decorator —— 一旦被呼叫即代表 guard 失效、走進了網路分支。
    private static Func<string, string> ThrowingDecorator
        => _ => throw new InvalidOperationException("queryDecorator must not be invoked on the empty-query early return");

    [Fact]
    public async Task ExecuteTravelSearch_MissingQueryProperty_FailsWithRequiredMessage()
    {
        var logger = Substitute.For<ILogger>();

        var result = await TravelSearchHelper.ExecuteTravelSearchAsync(
            Req("{\"args\":{\"locale\":\"zh-TW\",\"limit\":3}}"),
            mode: "train",
            sourceLabel: "tdx",
            queryDecorator: ThrowingDecorator,
            logger: logger);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("query is required.");
        result.RequestId.Should().Be("req_travel_1", "Fail 應原樣回傳 request 的 RequestId 供結果對應");
    }

    [Fact]
    public async Task ExecuteTravelSearch_EmptyStringQuery_Fails()
    {
        var logger = Substitute.For<ILogger>();

        var result = await TravelSearchHelper.ExecuteTravelSearchAsync(
            Req("{\"args\":{\"query\":\"\"}}"),
            "bus", "tdx", ThrowingDecorator, logger);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("query is required.");
    }

    [Fact]
    public async Task ExecuteTravelSearch_WhitespaceOnlyQuery_Fails()
    {
        // IsNullOrWhiteSpace：純空白應視同缺 query
        var logger = Substitute.For<ILogger>();

        var result = await TravelSearchHelper.ExecuteTravelSearchAsync(
            Req("{\"args\":{\"query\":\"   \"}}"),
            "train", "tdx", ThrowingDecorator, logger);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("query is required.");
    }

    [Fact]
    public async Task ExecuteTravelSearch_EmptyPayloadObject_Fails()
    {
        // payload "{}"：GetArgsElement 找不到 args/tool_args → 回 root，TryGetString("query") 回 null → guard 觸發
        var logger = Substitute.For<ILogger>();

        var result = await TravelSearchHelper.ExecuteTravelSearchAsync(
            Req("{}"),
            "train", "tdx", ThrowingDecorator, logger);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("query is required.");
    }

    [Fact]
    public async Task ExecuteTravelSearch_QueryAtTopLevelNotUnderArgs_Fails()
    {
        // query 放在 root 而非 args/tool_args 物件下：GetArgsElement 因無 args 物件回 root，
        // 但本案 root 同時無 args 物件 → 回 root → TryGetString 找得到 → 此案應「成功取得 query」。
        // 為避免觸網，這裡刻意「不放」query，僅放其他鍵，確認仍走 guard。
        var logger = Substitute.For<ILogger>();

        var result = await TravelSearchHelper.ExecuteTravelSearchAsync(
            Req("{\"locale\":\"zh-TW\",\"limit\":2}"),
            "bus", "tdx", ThrowingDecorator, logger);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("query is required.");
    }

    [Fact]
    public async Task ExecuteTravelSearch_LegacyToolArgsWithoutQuery_Fails()
    {
        // legacy 鍵 "tool_args" 也被 GetArgsElement 接受；但其中無 query → guard 觸發
        var logger = Substitute.For<ILogger>();

        var result = await TravelSearchHelper.ExecuteTravelSearchAsync(
            Req("{\"tool_args\":{\"locale\":\"en-US\"}}"),
            "train", "tdx", ThrowingDecorator, logger);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("query is required.");
    }

    [Fact]
    public async Task ExecuteTravelSearch_OutOfRangeLimitWithEmptyQuery_StillFailsCleanly()
    {
        // limit=999 會被 Math.Clamp(1,5) 收斂，但 clamp 在 query 檢查之前且無副作用，
        // 不應改變空 query 的早退結果。
        var logger = Substitute.For<ILogger>();

        var result = await TravelSearchHelper.ExecuteTravelSearchAsync(
            Req("{\"args\":{\"query\":\"\",\"limit\":999}}"),
            "train", "tdx", ThrowingDecorator, logger);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("query is required.");
    }

    [Fact]
    public async Task ExecuteTravelSearch_EarlyReturn_DoesNotInvokeDecorator()
    {
        // 反證 guard 真的短路：decorator 若被呼叫，旗標會翻 true（代表走進了網路分支）。
        var decoratorCalled = false;
        var logger = Substitute.For<ILogger>();

        var result = await TravelSearchHelper.ExecuteTravelSearchAsync(
            Req("{\"args\":{\"query\":\"\"}}"),
            "train", "tdx",
            queryDecorator: q => { decoratorCalled = true; return q; },
            logger: logger);

        result.Success.Should().BeFalse();
        decoratorCalled.Should().BeFalse("空 query 應在 queryDecorator(query) 之前就 return Fail");
    }

    [Fact]
    public async Task ExecuteTravelSearch_EarlyReturn_DoesNotTouchLogger()
    {
        // 早退路徑不應對 logger 有任何互動（LogDebug 只在 HTTP follow-fetch 失敗時才呼叫）。
        var logger = Substitute.For<ILogger>();

        var result = await TravelSearchHelper.ExecuteTravelSearchAsync(
            Req("{\"args\":{\"query\":\"   \"}}"),
            "bus", "tdx", ThrowingDecorator, logger);

        result.Success.Should().BeFalse();
        logger.ReceivedCalls().Should().BeEmpty("空 query 早退時不該記任何 log");
    }

    [Fact]
    public async Task ExecuteTravelSearch_PreservesGivenRequestIdOnFail()
    {
        // RequestId 透傳 —— 換一個 id 確認不是寫死字串
        var logger = Substitute.For<ILogger>();

        var result = await TravelSearchHelper.ExecuteTravelSearchAsync(
            Req("{\"args\":{\"query\":\"\"}}", requestId: "req_xyz_999"),
            "train", "tdx", ThrowingDecorator, logger);

        result.Success.Should().BeFalse();
        result.RequestId.Should().Be("req_xyz_999");
    }
}
