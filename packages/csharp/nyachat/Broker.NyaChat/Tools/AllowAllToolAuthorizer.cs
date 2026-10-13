using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// <see cref="INyaToolAuthorizer"/> 的 Nya 內建 fallback：一律允許。
/// </summary>
/// <remarks>
/// 「Nya 是插件，不是 Broker 的心臟」鐵則：當 broker 未提供治理用的
/// <c>BrokerToolAuthorizer</c> 時（例如 Nya 被單獨使用 / 測試），仍能運作。
/// broker 在 Program.cs 以自身實作覆蓋此 fallback，即把「只有管理者能用量化交易工具」
/// 這類治理決策收回 broker 側。
/// </remarks>
public sealed class AllowAllToolAuthorizer : INyaToolAuthorizer
{
    public bool CanUse(string userId, string channelType, NyaToolSchema tool) => true;
}
