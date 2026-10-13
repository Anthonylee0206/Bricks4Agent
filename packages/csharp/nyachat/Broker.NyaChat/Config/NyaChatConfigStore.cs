using System.Text.Json;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;

namespace Broker.NyaChat;

/// <summary>
/// <see cref="INyaConfigStore{T}"/> 的可重載實作。
///
/// 設計：快照替換（Snapshot Swap）。各服務透過 <see cref="Current"/> 取得當前生效的
/// <see cref="NyaChatConfig"/> 物件；<see cref="UpdateAsync"/> 建立全新物件並以單次參考指派
/// 原子替換，讀取端永遠看到一致快照、無需鎖。
///
/// 持久化：以單列 <see cref="NyaConfigEntry"/>（key=<see cref="ConfigKey"/>）存於 SQLite。
/// 啟動時若 DB 有覆寫則套用，否則使用 appsettings 反序列化得到的 seed。
/// </summary>
/// <remarks>
/// 僅提供機制與目前 <see cref="NyaChatConfig"/> 既有欄位的讀寫；
/// LlmProfiles / TaskRouting、token 預算新增欄位後，
/// 因 JSON 全量序列化，無需改動本類即自動隨配置持久化。
/// </remarks>
public sealed class NyaChatConfigStore : INyaConfigStore<NyaChatConfig>
{
    private const string ConfigKey = "nya:chat";

    private readonly BrokerDb _db;
    private readonly ILogger<NyaChatConfigStore> _logger;
    private volatile NyaChatConfig _current;

    public NyaChatConfigStore(NyaChatConfig seed, BrokerDb db, ILogger<NyaChatConfigStore> logger)
    {
        _db = db;
        _logger = logger;
        _db.EnsureTable<NyaConfigEntry>();
        _current = LoadOverlayOrSeed(seed);
    }

    public NyaChatConfig Current => _current;

    public Task UpdateAsync(NyaChatConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        Persist(config);
        _current = config; // 原子參考替換 → 後續 Current 立即生效
        _logger.LogInformation("NyaChat 配置已更新並持久化（key={Key}）。", ConfigKey);
        return Task.CompletedTask;
    }

    private NyaChatConfig LoadOverlayOrSeed(NyaChatConfig seed)
    {
        try
        {
            var row = _db.Get<NyaConfigEntry>(ConfigKey);
            if (row != null && !string.IsNullOrWhiteSpace(row.ConfigJson))
            {
                var overlay = JsonSerializer.Deserialize<NyaChatConfig>(row.ConfigJson);
                if (overlay != null)
                {
                    _logger.LogInformation("NyaChat 配置由 DB 覆寫載入（updated_at={At:u}）。", row.UpdatedAt);
                    return overlay;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "載入 NyaChat 配置覆寫失敗，回退使用 appsettings seed。");
        }
        return seed;
    }

    private void Persist(NyaChatConfig config)
    {
        var json = JsonSerializer.Serialize(config);
        // 單列覆寫：先刪後插，避免依賴 ORM 的 upsert 語意
        if (_db.Get<NyaConfigEntry>(ConfigKey) != null)
            _db.Delete<NyaConfigEntry>(ConfigKey);
        _db.Insert(new NyaConfigEntry
        {
            ConfigKey = ConfigKey,
            ConfigJson = json,
            UpdatedAt = DateTime.UtcNow
        });
    }
}
