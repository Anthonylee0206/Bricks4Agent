namespace Broker.NyaChat.Abstractions;

/// <summary>
/// 授權查詢抽象（guest gate，2026-07-08）：判斷來源是否為「正式使用者」。
/// false = guest（無有效身分組）→ Nya 靜默記錄、不回覆。
/// broker 以既有角色綁定鏈實作（BrokerPrincipalResolver）；Nya 內建 fallback 一律放行。
/// </summary>
public interface INyaPrincipalResolver
{
    bool IsAuthorized(string userId, string channelType);
}
