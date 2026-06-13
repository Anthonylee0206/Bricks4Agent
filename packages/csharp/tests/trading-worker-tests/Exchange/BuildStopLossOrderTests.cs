using TradingWorker.Exchange;
using TradingWorker.Models;

namespace TradingWorker.Tests.Exchange;

/// <summary>
/// finding I — BuildStopLossOrder 純函式:把開倉單推成「獨立」reduce-only STOP_MARKET 平倉保護單。
/// 進場成交後、SL 被拒不再丟掉已成交的進場(孤兒裸倉);這支函式鎖死 close-side 反向 / reduceOnly / 冪等後綴。
/// </summary>
public class BuildStopLossOrderTests
{
    private static PerpetualOrder Entry(string side, string posSide, string orderId = "op-abc123", decimal qty = 0.5m) => new()
    {
        OrderId = orderId, Symbol = "BTC-USDT", Exchange = "bingx",
        Side = side, PositionSide = posSide, OrderType = "market", Quantity = qty,
    };

    [Fact]
    public void LongEntry_ProducesSellLongReduceOnlyStopMarket()
    {
        var sl = BingxPerpetualClient.BuildStopLossOrder(Entry("buy", "long"), 60000m);
        sl.Side.Should().Be("sell", "平多 = SELL/LONG");
        sl.PositionSide.Should().Be("long");
        sl.ReduceOnly.Should().BeTrue();
        sl.OrderType.Should().Be("stop_market");
        sl.StopPrice.Should().Be(60000m);
        sl.Quantity.Should().Be(0.5m, "數量沿用進場單");
        sl.Symbol.Should().Be("BTC-USDT");
    }

    [Fact]
    public void ShortEntry_ProducesBuyShortReduceOnly()
    {
        var sl = BingxPerpetualClient.BuildStopLossOrder(Entry("sell", "short"), 65000m);
        sl.Side.Should().Be("buy", "平空 = BUY/SHORT");
        sl.PositionSide.Should().Be("short");
        sl.ReduceOnly.Should().BeTrue();
        sl.StopPrice.Should().Be(65000m);
    }

    [Fact]
    public void OrderId_GetsSlSuffix_ForIdempotency()
    {
        var sl = BingxPerpetualClient.BuildStopLossOrder(Entry("buy", "long", orderId: "op-abc123"), 60000m);
        sl.OrderId.Should().Be("op-abc123-sl", "沿用冪等:failover 重送同一保護單被 BingX 101400 擋");
    }

    [Fact]
    public void LongOrderId_TruncatedTo33_BeforeSuffix_StaysUnder36()
    {
        // 36 char 的 base → 截到 33 + "-sl"(3) = 36,不超過 BingX clientOrderID 上限
        var longId = new string('x', 36);
        var sl = BingxPerpetualClient.BuildStopLossOrder(Entry("buy", "long", orderId: longId), 60000m);
        sl.OrderId.Length.Should().BeLessThanOrEqualTo(36);
        sl.OrderId.Should().Be(new string('x', 33) + "-sl");
    }

    [Fact]
    public void EmptyOrderId_NoSuffix_StaysEmpty()
    {
        // 沒帶冪等 key 的進場 → SL 也不硬加後綴(維持 unique、不誤觸去重)
        var sl = BingxPerpetualClient.BuildStopLossOrder(Entry("buy", "long", orderId: ""), 60000m);
        sl.OrderId.Should().BeEmpty();
    }
}
