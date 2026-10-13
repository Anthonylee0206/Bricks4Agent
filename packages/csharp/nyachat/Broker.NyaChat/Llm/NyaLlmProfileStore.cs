using BrokerCore.Data;

namespace Broker.NyaChat;

/// <summary>
/// Doc 3 Plan A：LLM profile/route 專店（NyaChat 專屬 DB）。唯一真相：呼叫點 → route → profile，
/// 未配對落 "__default__"。取代 NyaChatConfig.LlmProfiles/TaskRouting + legacy 扁平欄位。
/// Doc 3 Plan C §11：可選 INyaSecretProtector（null=passthrough，既有 2-arg ctor 呼叫者零改動）。
/// </summary>
public sealed class NyaLlmProfileStore
{
    public const string DefaultCallSite = "__default__";

    private readonly BrokerDb _db;
    private readonly ILogger<NyaLlmProfileStore> _logger;
    private readonly INyaSecretProtector? _protector;

    public NyaLlmProfileStore(BrokerDb db, ILogger<NyaLlmProfileStore> logger, INyaSecretProtector? protector = null)
    {
        _db = db;
        _logger = logger;
        _protector = protector;
        _db.EnsureTable<NyaLlmProfileEntry>();
        _db.EnsureTable<NyaLlmRoute>();
    }

    // ── profile CRUD ─────────────────────────────────────────────────────────
    public void UpsertProfile(NyaLlmProfileEntry entry)
    {
        entry.UpdatedAt = DateTime.UtcNow;
        // 加密寫入「副本」，不就地改 caller 的 entry.ApiKey（避免 caller 事後讀到密文的 least-surprise 地雷）。
        var toStore = _protector == null ? entry : Encrypted(entry);
        var inserted = _db.Get<NyaLlmProfileEntry>(toStore.ProfileId) == null;
        if (inserted)
            _db.Insert(toStore);
        else
            _db.Update(toStore);
        // 應用程式 log（#4）：profile 異動留痕。絕不記 api_key——只記非敏感欄位。
        _logger.LogInformation("[NyaLlmProfileStore] Profile {Op}: id={ProfileId} provider={Provider} model={Model}",
            inserted ? "inserted" : "updated", entry.ProfileId, entry.Provider, entry.Model);
    }

    public NyaLlmProfileEntry? GetProfile(string profileId)
    {
        var entry = _db.Get<NyaLlmProfileEntry>(profileId);
        return entry == null ? null : Decrypted(entry);
    }

    /// <summary>ListProfiles 回傳 at-rest 形式（仍加密），由外層管理面遮罩後呈現，不洩明文。</summary>
    public List<NyaLlmProfileEntry> ListProfiles() => _db.GetAll<NyaLlmProfileEntry>();

    /// <summary>
    /// 刪除 profile；指向它的路由一併移除（回落 __default__）。
    /// __default__ 指向的 profile 拒刪（拋 InvalidOperationException）——否則所有未指定路由的呼叫點都會解析失敗。
    /// </summary>
    public bool DeleteProfile(string profileId)
    {
        if (_db.Get<NyaLlmRoute>(DefaultCallSite)?.ProfileId == profileId)
            throw new InvalidOperationException($"Profile「{profileId}」為系統預設使用中，無法刪除。");

        var deleted = _db.Execute("DELETE FROM nya_llm_profiles WHERE profile_id = @id", new { id = profileId }) > 0;
        if (deleted)
            _db.Execute("DELETE FROM nya_llm_routes WHERE profile_id = @id", new { id = profileId });
        _logger.LogInformation("[NyaLlmProfileStore] Profile delete: id={ProfileId} deleted={Deleted}", profileId, deleted);
        return deleted;
    }

    // ── route ────────────────────────────────────────────────────────────────
    /// <summary>設定路由；profile 不存在則拋 InvalidOperationException（避免寫出解析必敗的懸空路由）。</summary>
    public void SetRoute(string callSiteId, string profileId)
    {
        if (_db.Get<NyaLlmProfileEntry>(profileId) == null)
            throw new InvalidOperationException($"Profile「{profileId}」不存在。");

        var row = new NyaLlmRoute { CallSiteId = callSiteId, ProfileId = profileId, UpdatedAt = DateTime.UtcNow };
        if (_db.Get<NyaLlmRoute>(callSiteId) == null) _db.Insert(row);
        else _db.Update(row);
        _logger.LogInformation("[NyaLlmProfileStore] Route set: call_site={CallSiteId} → profile={ProfileId}", callSiteId, profileId);
    }

    /// <summary>移除呼叫點的專屬路由 → 回落 __default__（UI「（預設）」）。__default__ 本身不可移除。</summary>
    public bool ClearRoute(string callSiteId)
    {
        if (callSiteId == DefaultCallSite)
            throw new InvalidOperationException("系統預設路由不可清除。");

        var removed = _db.Execute("DELETE FROM nya_llm_routes WHERE call_site_id = @id", new { id = callSiteId }) > 0;
        _logger.LogInformation("[NyaLlmProfileStore] Route cleared: call_site={CallSiteId} removed={Removed}", callSiteId, removed);
        return removed;
    }

    public List<NyaLlmRoute> ListRoutes() => _db.GetAll<NyaLlmRoute>();

    // ── 遷移 ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// 店空時，用既有 router 把三個 legacy 任務的等效 profile 持久化（忠實保留解析結果），
    /// 並設 __default__ → chat。一次性、冪等（店非空即跳過，回 false）。
    /// </summary>
    public bool MigrateFromLegacyIfEmpty(NyaChatConfig legacyConfig, INyaModelRouter router)
    {
        if (_db.GetAll<NyaLlmProfileEntry>().Count > 0)
            return false;

        // task 名即 call_site_id（chat / fact_extraction / summarization）。
        foreach (var task in new[] { NyaLlmTasks.Chat, NyaLlmTasks.FactExtraction, NyaLlmTasks.Summarization })
        {
            var effective = router.Resolve(task, legacyConfig);
            UpsertProfile(NyaLlmProfileEntry.FromProfile(task, effective.Provider, effective));
            SetRoute(task, task);
        }
        SetRoute(DefaultCallSite, NyaLlmTasks.Chat);

        _logger.LogInformation("[NyaLlmProfileStore] Migrated legacy config → store (chat/fact_extraction/summarization + __default__).");
        return true;
    }

    // ── 解析 ─────────────────────────────────────────────────────────────────
    /// <summary>call_site_id → route → profile；查無 route 落 __default__；都查無則拋（mis-seed 守衛）。</summary>
    public NyaLlmProfile ResolveProfile(string callSiteId)
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

    // ── 解密輔助 ─────────────────────────────────────────────────────────────
    /// <summary>回傳 entry 的解密副本（_protector=null 時直接回原物件）。不修改 DB 存放的密文。</summary>
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

    /// <summary>回傳一份 ApiKey 已加密的副本（寫入用）；_protector 為 null 時呼叫端不會走到此路徑。</summary>
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
