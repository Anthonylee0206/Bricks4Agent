using System.Security.Cryptography;
using System.Text;
using BrokerCore.Crypto;

namespace Unit.Tests.Crypto;

/// <summary>
/// At-rest AES-256-GCM 密文(存 DB 的 exchange API key/secret 等)。安全核心、原本 0% 覆蓋。
/// 重點驗 round-trip + **AAD 綁定**(把密文搬到別筆紀錄就解不開)+ key/格式邊界。
/// </summary>
public class AtRestSecretCryptoTests
{
    private static string KeyA() => Convert.ToBase64String(Encoding.ASCII.GetBytes("0123456789ABCDEF0123456789ABCDEF")); // 32B
    private static string KeyB() => Convert.ToBase64String(Encoding.ASCII.GetBytes("FEDCBA9876543210FEDCBA9876543210")); // 32B

    [Fact]
    public void RoundTrip_SameAad_RecoversPlaintext()
    {
        var c = new AtRestSecretCrypto(KeyA());
        var enc = c.Encrypt("super-secret-api-key", "credential:42:bingx");
        c.Decrypt(enc, "credential:42:bingx").Should().Be("super-secret-api-key");
    }

    [Fact]
    public void Decrypt_WrongAad_Throws()
    {
        // AAD 綁定:換 context(ex: 換成別筆 credential id)→ tag 對不起來、解不開
        var c = new AtRestSecretCrypto(KeyA());
        var enc = c.Encrypt("k", "credential:42:bingx");
        ((Action)(() => c.Decrypt(enc, "credential:99:bingx"))).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_WrongKey_Throws()
    {
        var enc = new AtRestSecretCrypto(KeyA()).Encrypt("k", "ctx");
        ((Action)(() => new AtRestSecretCrypto(KeyB()).Decrypt(enc, "ctx"))).Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Encrypt_RandomNonce_DifferentCiphertextSamePlaintext()
    {
        var c = new AtRestSecretCrypto(KeyA());
        var e1 = c.Encrypt("same", "ctx");
        var e2 = c.Encrypt("same", "ctx");
        e1.Should().NotBe(e2, "隨機 nonce → 同明文也產不同密文");
        c.Decrypt(e1, "ctx").Should().Be("same");
        c.Decrypt(e2, "ctx").Should().Be("same");
    }

    [Fact]
    public void EmptyPlaintext_RoundTrips()
    {
        var c = new AtRestSecretCrypto(KeyA());
        c.Decrypt(c.Encrypt("", "ctx"), "ctx").Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Ctor_BlankKey_Throws(string key)
        => ((Action)(() => new AtRestSecretCrypto(key))).Should().Throw<ArgumentException>();

    [Fact]
    public void Ctor_WrongKeySize_Throws()
        // 16 bytes(AES-128)不接受、必須 32 bytes(AES-256)
        => ((Action)(() => new AtRestSecretCrypto(Convert.ToBase64String(new byte[16]))))
            .Should().Throw<InvalidOperationException>();

    [Fact]
    public void Decrypt_TooShortData_Throws()
        => ((Action)(() => new AtRestSecretCrypto(KeyA()).Decrypt(Convert.ToBase64String(new byte[4]), "ctx")))
            .Should().Throw<CryptographicException>();
}
