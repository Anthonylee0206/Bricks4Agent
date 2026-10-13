using System.Text.Json;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;

namespace Broker.Services.NyaTools;

public sealed class BrokerTierToolAuthorizer : INyaToolAuthorizer
{
    public const string QueryPermission = "query";
    public const string TransportPermission = "transport";

    private readonly BrokerDb _db;
    private readonly ILogger<BrokerTierToolAuthorizer> _logger;

    public BrokerTierToolAuthorizer(BrokerDb db, ILogger<BrokerTierToolAuthorizer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public bool CanUse(string userId, string channelType, NyaToolSchema tool)
    {
        if (string.IsNullOrWhiteSpace(tool.RequiredPermission))
            return true;

        var allowed = HasPermission(userId, channelType, tool.RequiredPermission);
        if (!allowed)
            _logger.LogInformation(
                "[BrokerTierToolAuthorizer] Denied tool {Tool} (perm={Perm}) for user {User}/{Channel}",
                tool.Name, tool.RequiredPermission, userId, channelType);
        return allowed;
    }

    private bool HasPermission(string userId, string channelType, string requiredPermission)
    {
        try
        {
            var permissions = LoadEffectivePermissions(userId, channelType);
            if (permissions == null)
                return false;

            return requiredPermission.ToLowerInvariant() switch
            {
                QueryPermission => permissions.AllowQuery,
                TransportPermission => permissions.AllowTransport,
                _ => false
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[BrokerTierToolAuthorizer] Permission lookup failed for user {User}/{Channel} perm={Perm}; denying.",
                userId, channelType, requiredPermission);
            return false;
        }
    }

    private HighLevelUserPermissions? LoadEffectivePermissions(string userId, string channelType)
    {
        var channel = channelType.ToLowerInvariant();
        var prefix = channel + ":";
        var channelUserId = userId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? userId[prefix.Length..]
            : userId;

        var json = _db.Scalar<string>(
            $"SELECT content_ref FROM shared_context_entries WHERE document_id = @docId AND {SystemContextDocuments.TrustedGlobalCondition()} ORDER BY version DESC LIMIT 1",
            new { docId = $"hlm.profile.{channel}.{channelUserId}" });
        if (string.IsNullOrWhiteSpace(json))
            return null;

        var profile = JsonSerializer.Deserialize<HighLevelUserProfile>(json);
        if (profile == null ||
            profile.RegistrationStatus is HighLevelRegistrationStatus.Rejected or HighLevelRegistrationStatus.DeniedByPolicy)
            return null;

        return (profile.Permissions ?? HighLevelUserPermissions.CreateDefault()).EffectiveForTier(profile.AccessTier);
    }
}
