using BrokerCore;
using BrokerCore.Data;

namespace Broker.Services.NyaTools;

/// <summary>
/// 身分映射服務（寄宿功課 Phase 0-A）：建立 / 撤銷 / 解析 Nya 使用者 → Principal 綁定。
/// </summary>
/// <remarks>
/// 解析規則：只認未撤銷綁定；精確通道優先於 <c>"*"</c> 萬用；同優先級取最新建立者。
/// 每次呼叫都打 DB（不快取）——撤銷必須在進行中對話的下一個工具呼叫立即生效（0-C 雙時點）。
/// </remarks>
public sealed class NyaPrincipalBindingService
{
    private readonly BrokerDb _db;
    private readonly ILogger<NyaPrincipalBindingService> _logger;

    public NyaPrincipalBindingService(BrokerDb db, ILogger<NyaPrincipalBindingService> logger)
    {
        _db = db;
        _logger = logger;
        _db.EnsureTable<NyaPrincipalBinding>();
    }

    /// <summary>建立綁定（管理端專用——綁定即授權，不開放 LLM 工具）。</summary>
    public NyaPrincipalBinding Bind(string userId, string channelType, string principalId, string createdBy)
    {
        var binding = new NyaPrincipalBinding
        {
            BindingId = IdGen.New("nyb"),
            UserId = userId,
            ChannelType = string.IsNullOrWhiteSpace(channelType) ? "*" : channelType,
            PrincipalId = principalId,
            CreatedBy = createdBy,
            CreatedAt = DateTime.UtcNow
        };
        _db.Insert(binding);
        _logger.LogInformation("[NyaPrincipalBinding] Bound {User}/{Channel} → {Principal} by {By}",
            userId, binding.ChannelType, principalId, createdBy);
        return binding;
    }

    /// <summary>撤銷綁定（soft-delete）。回傳 false = 綁定不存在或已撤銷。</summary>
    public bool Revoke(string bindingId)
    {
        var affected = _db.Execute(
            "UPDATE nya_principal_bindings SET revoked_at = @now WHERE binding_id = @bindingId AND revoked_at IS NULL",
            new { now = DateTime.UtcNow, bindingId });
        if (affected > 0)
            _logger.LogInformation("[NyaPrincipalBinding] Revoked binding {BindingId}", bindingId);
        return affected > 0;
    }

    /// <summary>
    /// 解析 (userId, channelType) 的生效 PrincipalId；無有效綁定回 null。
    /// 精確通道優先於 "*"；同優先級取最新。
    /// </summary>
    public string? Resolve(string userId, string channelType)
    {
        var binding = _db.QueryFirst<NyaPrincipalBinding>(
            @"SELECT * FROM nya_principal_bindings
              WHERE user_id = @userId
                AND (channel_type = @channelType OR channel_type = '*')
                AND revoked_at IS NULL
              ORDER BY CASE WHEN channel_type = @channelType THEN 0 ELSE 1 END, created_at DESC
              LIMIT 1",
            new { userId, channelType });
        return binding?.PrincipalId;
    }

    /// <summary>列出全部有效（未撤銷）綁定——告警推播收件人解析用（Phase 2-B）。</summary>
    public List<NyaPrincipalBinding> ListActiveBindings()
        => _db.Query<NyaPrincipalBinding>(
            "SELECT * FROM nya_principal_bindings WHERE revoked_at IS NULL ORDER BY created_at DESC", null);

    /// <summary>列出某使用者的綁定（含已撤銷，管理端檢視用）。</summary>
    public List<NyaPrincipalBinding> ListByUser(string userId)
        => _db.Query<NyaPrincipalBinding>(
            "SELECT * FROM nya_principal_bindings WHERE user_id = @userId ORDER BY created_at DESC",
            new { userId });
}
