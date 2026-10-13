using System.Security.Cryptography;
using System.Text;

namespace BrokerCore.Crypto;

/// <summary>
/// Master-key AES-256-GCM 加密、給「at rest」存資料庫的小型敏感欄位用
/// （exchange API key / secret 等）。同一支 master key 解 ECDH session 也用、
/// 但 at-rest 跟 session 加密的 AAD 不同、密文互不通用。
///
/// 格式：Base64(nonce[12] | ciphertext | tag[16])
/// AAD 帶呼叫者指定的「綁定字串」（ex: "credential:{id}:{exchange}"），
/// 防止把密文搬到別筆紀錄就能解。改 id / exchange 後解密會失敗（tag 對不起來）。
/// </summary>
public sealed class AtRestSecretCrypto
{
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private readonly byte[] _masterKey;

    public AtRestSecretCrypto(string masterKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(masterKeyBase64))
            throw new ArgumentException("Broker master key must be configured.", nameof(masterKeyBase64));

        _masterKey = Convert.FromBase64String(masterKeyBase64);
        if (_masterKey.Length != 32)
            throw new InvalidOperationException("Broker master key must decode to exactly 32 bytes (AES-256).");
    }

    public string Encrypt(string plaintext, string aadContext)
    {
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var ct = new byte[pt.Length];
        var tag = new byte[TagLength];
        var aad = Encoding.UTF8.GetBytes(aadContext);

        using var aes = new AesGcm(_masterKey, TagLength);
        aes.Encrypt(nonce, pt, ct, tag, aad);

        var result = new byte[NonceLength + ct.Length + TagLength];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceLength);
        Buffer.BlockCopy(ct, 0, result, NonceLength, ct.Length);
        Buffer.BlockCopy(tag, 0, result, NonceLength + ct.Length, TagLength);
        return Convert.ToBase64String(result);
    }

    public string Decrypt(string encryptedBase64, string aadContext)
    {
        var data = Convert.FromBase64String(encryptedBase64);
        if (data.Length < NonceLength + TagLength)
            throw new CryptographicException("Encrypted secret too short.");

        var nonce = data.AsSpan(0, NonceLength);
        var ctLen = data.Length - NonceLength - TagLength;
        var ct = data.AsSpan(NonceLength, ctLen);
        var tag = data.AsSpan(NonceLength + ctLen, TagLength);
        var aad = Encoding.UTF8.GetBytes(aadContext);
        var pt = new byte[ctLen];

        using var aes = new AesGcm(_masterKey, TagLength);
        aes.Decrypt(nonce, ct, tag, pt, aad);
        return Encoding.UTF8.GetString(pt);
    }
}
