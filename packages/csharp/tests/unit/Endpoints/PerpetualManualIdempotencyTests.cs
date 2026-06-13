using Broker.Endpoints;

namespace Unit.Tests.Endpoints;

/// <summary>
/// finding F — 人工 POST /perpetual/order 的冪等 key 純函式(DeriveManualOrderKey)。
/// 同一下單意圖 → 同 key → BingX 以 101400 擋重複(重試 / 連點 / 前端重送不會雙下真錢單)。
/// </summary>
public class PerpetualManualIdempotencyTests
{
    private static string Key(
        string owner = "prn_a", string exchange = "bingx", string symbol = "BTC-USDT",
        string side = "buy", string positionSide = "long",
        decimal qty = 0.01m, bool reduceOnly = false, long bucket = 12345L)
        => PerpetualEndpoints.DeriveManualOrderKey(owner, exchange, symbol, side, positionSide, qty, reduceOnly, bucket);

    [Fact]
    public void SameIntent_SameBucket_SameKey()
        => Key().Should().Be(Key(), "同意圖同桶 → 同 key、重送被 BingX 擋");

    [Fact]
    public void DiffSide_DiffKey()
        => Key(side: "buy").Should().NotBe(Key(side: "sell"));

    [Fact]
    public void DiffPositionSide_DiffKey()
        => Key(positionSide: "long").Should().NotBe(Key(positionSide: "short"));

    [Fact]
    public void DiffReduceOnly_DiffKey()
        // 開倉(false)與平倉(true)即使同 symbol/side 也不能撞同 key、否則平倉意圖被當成開倉重送
        => Key(reduceOnly: false).Should().NotBe(Key(reduceOnly: true));

    [Fact]
    public void DiffQuantity_DiffKey()
        => Key(qty: 0.01m).Should().NotBe(Key(qty: 0.02m));

    [Fact]
    public void DiffBucket_DiffKey()
        // 跨 5 分鐘窗 = 新意圖、不被誤去重
        => Key(bucket: 12345L).Should().NotBe(Key(bucket: 12346L));

    [Fact]
    public void Format_Under36Chars_WithManualPrefix()
    {
        var k = Key();
        k.Length.Should().BeLessThanOrEqualTo(36, "BingX clientOrderID 上限");
        k.Should().StartWith("m-", "manual 前綴、與自動 op-/cl-/-sl 不撞");
    }
}
