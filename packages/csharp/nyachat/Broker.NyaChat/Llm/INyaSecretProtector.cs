namespace Broker.NyaChat;

/// <summary>api_key 等敏感字串的對稱保護（Doc 3 Plan C §11）。store 注入；null=不加密（passthrough）。</summary>
public interface INyaSecretProtector
{
    string Protect(string plain);
    string Unprotect(string stored);
}

/// <summary>遮罩純函式：保留尾 4 碼、其餘以 * 取代；短字串一律 "****"。</summary>
public static class NyaSecretMask
{
    public static string Mask(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length <= 4) return "****";
        return new string('*', Math.Min(8, key.Length - 4)) + key[^4..];
    }
}
