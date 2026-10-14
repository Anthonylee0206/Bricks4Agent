using BrokerCore.Data;

namespace Broker.NyaChat;

public sealed class NyaLlmProfileStore
{
    public const string DefaultCallSite = "__default__";

    private readonly BrokerDb _db;
    private readonly ILogger<NyaLlmProfileStore> _logger;
    private readonly INyaSecretProtector? _protector;
    private readonly object _gate = new();

    public bool TryCreateProfile(NyaLlmProfileEntry entry)
    {
        lock (_gate)
        {
            if (_db.Get<NyaLlmProfileEntry>(entry.ProfileId) != null) return false;
            UpsertProfile(entry);
            return true;
        }
    }

    public bool TryUpdateProfile(NyaLlmProfileEntry entry)
    {
        lock (_gate)
        {
            var existing = GetProfile(entry.ProfileId);
            if (existing == null) return false;
            entry.CreatedAt = existing.CreatedAt;
            if (string.IsNullOrWhiteSpace(entry.ApiKey) || entry.ApiKey.Contains('*')) entry.ApiKey = existing.ApiKey;
            UpsertProfile(entry);
            return true;
        }
    }

    public NyaLlmProfileStore(BrokerDb db, ILogger<NyaLlmProfileStore> logger, INyaSecretProtector? protector = null)
    {
        _db = db;
        _logger = logger;
        _protector = protector;
        _db.EnsureTable<NyaLlmProfileEntry>();
        _db.EnsureTable<NyaLlmRoute>();
    }

    public void UpsertProfile(NyaLlmProfileEntry entry)
    {
        lock (_gate)
        {
            var extraError = NyaLlmAdminSettings.ValidateExtraBody(entry.ParamsJson);
            if (extraError != null) throw new InvalidOperationException(extraError);
            entry.UpdatedAt = DateTime.UtcNow;

            var toStore = _protector == null ? entry : Encrypted(entry);
            var inserted = _db.Get<NyaLlmProfileEntry>(toStore.ProfileId) == null;
            if (inserted)
                _db.Insert(toStore);
            else
                _db.Update(toStore);

            _logger.LogInformation("[NyaLlmProfileStore] Profile {Op}: id={ProfileId} provider={Provider} model={Model}",
                inserted ? "inserted" : "updated", entry.ProfileId, entry.Provider, entry.Model);
        }
    }

    public NyaLlmProfileEntry? GetProfile(string profileId)
    {
        var entry = _db.Get<NyaLlmProfileEntry>(profileId);
        return entry == null ? null : Decrypted(entry);
    }

        public List<NyaLlmProfileEntry> ListProfiles() => _db.GetAll<NyaLlmProfileEntry>();

        public bool DeleteProfile(string profileId)
    {
        lock (_gate)
        {
            if (_db.Get<NyaLlmRoute>(DefaultCallSite)?.ProfileId == profileId)
                throw new InvalidOperationException($"Profile「{profileId}」為系統預設使用中，無法刪除。");

            var deleted = _db.Execute("DELETE FROM nya_llm_profiles WHERE profile_id = @id", new { id = profileId }) > 0;
            if (deleted)
                _db.Execute("DELETE FROM nya_llm_routes WHERE profile_id = @id", new { id = profileId });
            _logger.LogInformation("[NyaLlmProfileStore] Profile delete: id={ProfileId} deleted={Deleted}", profileId, deleted);
            return deleted;
        }
    }

        public void SetRoute(string callSiteId, string profileId)
    {
        lock (_gate)
        {
            if (_db.Get<NyaLlmProfileEntry>(profileId) == null)
                throw new InvalidOperationException($"Profile「{profileId}」不存在。");

            var row = new NyaLlmRoute { CallSiteId = callSiteId, ProfileId = profileId, UpdatedAt = DateTime.UtcNow };
            if (_db.Get<NyaLlmRoute>(callSiteId) == null) _db.Insert(row);
            else _db.Update(row);
            _logger.LogInformation("[NyaLlmProfileStore] Route set: call_site={CallSiteId} → profile={ProfileId}", callSiteId, profileId);
        }
    }

        public bool ClearRoute(string callSiteId)
    {
        lock (_gate)
        {
            if (callSiteId == DefaultCallSite)
                throw new InvalidOperationException("系統預設路由不可清除。");

            var removed = _db.Execute("DELETE FROM nya_llm_routes WHERE call_site_id = @id", new { id = callSiteId }) > 0;
            _logger.LogInformation("[NyaLlmProfileStore] Route cleared: call_site={CallSiteId} removed={Removed}", callSiteId, removed);
            return removed;
        }
    }

    public List<NyaLlmRoute> ListRoutes() => _db.GetAll<NyaLlmRoute>();

        public bool MigrateFromLegacyIfEmpty(NyaChatConfig legacyConfig, INyaModelRouter router)
    {
        if (_db.GetAll<NyaLlmProfileEntry>().Count > 0)
            return false;

        foreach (var task in new[] { NyaLlmTasks.Chat, NyaLlmTasks.Summarization })
        {
            var effective = router.Resolve(task, legacyConfig);
            UpsertProfile(NyaLlmProfileEntry.FromProfile(task, effective.Provider, effective));
            SetRoute(task, task);
        }
        SetRoute(DefaultCallSite, NyaLlmTasks.Chat);

        _logger.LogInformation("[NyaLlmProfileStore] Migrated legacy config → store (chat/fact_extraction/summarization + __default__).");
        return true;
    }

        public NyaLlmProfile ResolveProfile(string callSiteId)
    {
        lock (_gate)
        {
            var route = _db.Get<NyaLlmRoute>(callSiteId) ?? _db.Get<NyaLlmRoute>(DefaultCallSite);
            if (route == null)
                throw new InvalidOperationException(
                    $"No LLM route for call site '{callSiteId}' and no '{DefaultCallSite}' fallback configured. " +
                    $"Profile store is mis-seeded — run migration / set a default profile.");

            var entry = _db.Get<NyaLlmProfileEntry>(route.ProfileId)
                ?? throw new InvalidOperationException(
                    $"LLM route '{route.CallSiteId}' points to missing profile '{route.ProfileId}'.");
            return Decrypted(entry).ToProfile();
        }
    }

        private NyaLlmProfileEntry Decrypted(NyaLlmProfileEntry e)
    {
        if (_protector == null) return e;
        return new NyaLlmProfileEntry
        {
            ProfileId  = e.ProfileId,
            Provider   = e.Provider,
            BaseUrl    = e.BaseUrl,
            ApiKey     = _protector.Unprotect(e.ApiKey),
            Model      = e.Model,
            ParamsJson = e.ParamsJson,
            Template   = e.Template,
            CreatedAt  = e.CreatedAt,
            UpdatedAt  = e.UpdatedAt
        };
    }

        private NyaLlmProfileEntry Encrypted(NyaLlmProfileEntry e)
        => new()
        {
            ProfileId  = e.ProfileId,
            Provider   = e.Provider,
            BaseUrl    = e.BaseUrl,
            ApiKey     = _protector!.Protect(e.ApiKey),
            Model      = e.Model,
            ParamsJson = e.ParamsJson,
            Template   = e.Template,
            CreatedAt  = e.CreatedAt,
            UpdatedAt  = e.UpdatedAt
        };
}
