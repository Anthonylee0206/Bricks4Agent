using Broker.NyaChat;
using BrokerCore.Crypto;

namespace Broker.Services.NyaTools;

/// <summary>
/// 以 broker 既有 AtRestSecretCrypto 實作 INyaSecretProtector（Doc 3 Plan C §11）。
/// AAD 固定為 "nya_llm_api_key"，保證密文與其他用途的 at-rest 加密不互通。
/// </summary>
public sealed class AtRestSecretProtector : INyaSecretProtector
{
    private const string Aad = "nya_llm_api_key";

    private readonly AtRestSecretCrypto _crypto;
    public AtRestSecretProtector(AtRestSecretCrypto crypto) => _crypto = crypto;

    public string Protect(string plain) => string.IsNullOrEmpty(plain) ? plain : _crypto.Encrypt(plain, Aad);
    public string Unprotect(string stored) => string.IsNullOrEmpty(stored) ? stored : _crypto.Decrypt(stored, Aad);
}
