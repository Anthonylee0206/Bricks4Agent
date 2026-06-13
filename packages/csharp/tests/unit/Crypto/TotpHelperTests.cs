using System.Text;
using BrokerCore.Crypto;

namespace Unit.Tests.Crypto;

/// <summary>
/// TOTP 2FA(RFC 6238、HMAC-SHA1、30s step、6 位)契約。安全核心、原本 0% 覆蓋。
/// 用 RFC 6238 Appendix B 官方測試向量驗「演算法正確」(非僅 round-trip),再覆蓋容錯窗 / 格式 / Base32。
/// </summary>
public class TotpHelperTests
{
    // RFC 6238 標準 secret = ASCII "12345678901234567890";官方 8-digit TOTP 取後 6 碼(本實作 6 位)
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]          // RFC 8-digit 94287082
    [InlineData(1234567890L, "005924")]  // RFC 8-digit 89005924
    [InlineData(2000000000L, "279037")]  // RFC 8-digit 69279037
    public void GenerateCode_MatchesRfc6238Vectors(long unixSeconds, string expected)
    {
        var utc = DateTime.UnixEpoch.AddSeconds(unixSeconds);
        TotpHelper.GenerateCode(RfcSecret, utc).Should().Be(expected, "必須符合 RFC 6238 官方向量");
    }

    [Fact]
    public void GenerateCode_IsDeterministic_And6Digits()
    {
        var now = DateTime.UnixEpoch.AddSeconds(1_700_000_000);
        var a = TotpHelper.GenerateCode(RfcSecret, now);
        TotpHelper.GenerateCode(RfcSecret, now).Should().Be(a, "同 secret 同時間 → 同 code");
        a.Should().MatchRegex(@"^\d{6}$");
    }

    // ── Verify:容 ±1 步、拒超窗 / 格式錯 ──────────────────────────

    [Fact]
    public void Verify_AcceptsCurrentCode()
    {
        var now = DateTime.UnixEpoch.AddSeconds(1_700_000_000);
        TotpHelper.Verify(RfcSecret, TotpHelper.GenerateCode(RfcSecret, now), now).Should().BeTrue();
    }

    [Fact]
    public void Verify_ToleratesOneStepDrift()
    {
        var t0 = DateTime.UnixEpoch.AddSeconds(1_700_000_000);
        // 前一步(-30s)與後一步(+30s)的 code 在 now 都應通過(時鐘漂移容錯)
        TotpHelper.Verify(RfcSecret, TotpHelper.GenerateCode(RfcSecret, t0.AddSeconds(-30)), t0).Should().BeTrue();
        TotpHelper.Verify(RfcSecret, TotpHelper.GenerateCode(RfcSecret, t0.AddSeconds(30)), t0).Should().BeTrue();
    }

    [Fact]
    public void Verify_RejectsBeyondDriftWindow()
    {
        var t0 = DateTime.UnixEpoch.AddSeconds(1_700_000_000);
        // -90s = step-3、超過 ±1 容錯 → 拒(這也涵蓋「格式正確但值不符」的分支)
        TotpHelper.Verify(RfcSecret, TotpHelper.GenerateCode(RfcSecret, t0.AddSeconds(-90)), t0).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("12345")]    // 太短
    [InlineData("1234567")]  // 太長
    public void Verify_RejectsMalformed(string? code)
    {
        var now = DateTime.UnixEpoch.AddSeconds(1_700_000_000);
        TotpHelper.Verify(RfcSecret, code!, now).Should().BeFalse();
    }

    // ── Base32(RFC 4648)──────────────────────────────────────────

    [Fact]
    public void Base32_KnownVector()
        => TotpHelper.ToBase32(Encoding.ASCII.GetBytes("foo")).Should().Be("MZXW6", "RFC 4648 'foo' 不含 padding");

    [Fact]
    public void Base32_RoundTrips()
    {
        var secret = TotpHelper.GenerateSecret();
        TotpHelper.FromBase32(TotpHelper.ToBase32(secret)).Should().Equal(secret);
    }

    [Fact]
    public void FromBase32_TolerantOfCaseSpacePadding()
        => TotpHelper.FromBase32("mz xw6=").Should().Equal(TotpHelper.FromBase32("MZXW6"));

    [Fact]
    public void FromBase32_InvalidChar_Throws()
        => ((Action)(() => TotpHelper.FromBase32("MZXW61"))).Should().Throw<FormatException>(); // '1' 不在 base32 字母表

    // ── secret / otpauth url / backup codes ──────────────────────

    [Fact]
    public void GenerateSecret_Is20BytesAndRandom()
    {
        var s1 = TotpHelper.GenerateSecret();
        var s2 = TotpHelper.GenerateSecret();
        s1.Should().HaveCount(20);
        s1.Should().NotEqual(s2, "每次應產不同 secret");
    }

    [Fact]
    public void BuildOtpAuthUrl_ContainsRequiredFields()
    {
        var url = TotpHelper.BuildOtpAuthUrl(RfcSecret, "alice@b4a", "B4A Broker");
        url.Should().StartWith("otpauth://totp/");
        url.Should().Contain("secret=" + TotpHelper.ToBase32(RfcSecret));
        url.Should().Contain("algorithm=SHA1").And.Contain("digits=6").And.Contain("period=30");
        url.Should().Contain("issuer=");
    }

    [Fact]
    public void GenerateBackupCodes_CountLengthAndNoConfusableChars()
    {
        var codes = TotpHelper.GenerateBackupCodes();
        codes.Should().HaveCount(8, "預設 8 個");
        codes.Should().OnlyContain(c => c.Length == 10);
        // 去掉易混淆 I / O / 0 / 1
        codes.Should().OnlyContain(c => !c.Contains('I') && !c.Contains('O') && !c.Contains('0') && !c.Contains('1'));
        TotpHelper.GenerateBackupCodes(3).Should().HaveCount(3);
    }
}
