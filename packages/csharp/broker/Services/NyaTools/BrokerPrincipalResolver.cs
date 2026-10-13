using Broker.NyaChat.Abstractions;
using BrokerCore.Data;

namespace Broker.Services.NyaTools;

/// <summary>
/// guest gate 的 broker 實作：至少有一個有效身分組才是正式使用者。
/// 撤銷全部身分組 = 回到 guest（principal 綁定仍在也一樣），與後台 GUEST 標示一致。
/// 判定鏈與工具判權共用 <see cref="BrokerToolAuthorizer.ActiveRoles"/>，不另立權限邏輯。
/// </summary>
public sealed class BrokerPrincipalResolver : INyaPrincipalResolver
{
    private readonly BrokerDb _db;
    private readonly NyaPrincipalBindingService _bindings;

    public BrokerPrincipalResolver(BrokerDb db, NyaPrincipalBindingService bindings)
    {
        _db = db;
        _bindings = bindings;
    }

    public bool IsAuthorized(string userId, string channelType)
        => BrokerToolAuthorizer.ActiveRoles(_db, _bindings, userId, channelType).Count > 0;
}
