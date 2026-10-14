using System.Text.Json;
using System.Text.Json.Nodes;
using Broker.NyaChat.Abstractions;
using BrokerCore.Data;

namespace Broker.NyaChat;

public sealed class NyaChatConfigStore : INyaConfigStore<NyaChatConfig>
{
    private const string ConfigKey = "nya:chat";
    private readonly BrokerDb _db;
    private readonly ILogger<NyaChatConfigStore> _logger;
    private readonly NyaChatConfig _seed;
    private readonly object _gate = new();
    private JsonObject _overrides = new();
    private volatile NyaChatConfig _current;

    public NyaChatConfigStore(NyaChatConfig seed, BrokerDb db, ILogger<NyaChatConfigStore> logger)
    {
        _db = db;
        _logger = logger;
        _seed = Clone(seed);
        _db.EnsureTable<NyaConfigEntry>();
        _db.EnsureTable<NyaAuditLog>();
        _current = Clone(_seed);
        try
        {
            var row = _db.Get<NyaConfigEntry>(ConfigKey);
            if (row != null && JsonNode.Parse(row.ConfigJson) is JsonObject stored)
            {
                foreach (var spec in NyaChatHotSettings.All)
                    if (stored.TryGetPropertyValue(spec.Key, out var value))
                        _overrides[spec.Key] = value?.DeepClone();
                if (stored.TryGetPropertyValue(nameof(NyaChatConfig.Enabled), out var enabled) && enabled is JsonValue)
                    _overrides[nameof(NyaChatConfig.Enabled)] = enabled.DeepClone();
            }
            _current = ApplyOverrides(_overrides);
        }
        catch (Exception ex)
        {
            _overrides = new();
            _logger.LogWarning(ex, "載入 NyaChat 設定失敗，使用 appsettings 預設。");
        }
    }

    public NyaChatConfig Current => _current;

    public object Effective()
    {
        lock (_gate)
        {
            var values = JsonSerializer.SerializeToNode(_current)!.AsObject();
            var defaults = JsonSerializer.SerializeToNode(_seed)!.AsObject();
            return new
            {
                settings = NyaChatHotSettings.All.Select(spec => new
                {
                    key = spec.Key, label = spec.Label, type = spec.Type, min = spec.Min, max = spec.Max,
                    description = spec.Description, value = values[spec.Key]?.DeepClone(),
                    default_value = defaults[spec.Key]?.DeepClone()
                }).ToArray()
            };
        }
    }

    public Task<string[]> PatchAsync(JsonElement patch, string username, string role, CancellationToken ct)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            var merged = NyaChatHotSettings.Merge(_current, patch, out var changed);
            var updated = merged.Deserialize<NyaChatConfig>()!;
            var overrides = (JsonObject)_overrides.DeepClone();
            foreach (var key in changed) overrides[key] = merged[key]?.DeepClone();
            _db.InTransaction(() =>
            {
                Persist(overrides);
                Audit("config_update", changed, username, role);
            });
            _overrides = overrides;
            _current = updated;
            return Task.FromResult(changed);
        }
    }

    public Task ResetAsync(string username, string role, CancellationToken ct)
    {
        lock (_gate)
        {
            ct.ThrowIfCancellationRequested();
            var retained = (JsonObject)_overrides.DeepClone();
            var changed = NyaChatHotSettings.All.Select(s => s.Key).Where(k => retained.Remove(k)).ToArray();
            _db.InTransaction(() =>
            {
                if (retained.Count == 0) _db.Delete<NyaConfigEntry>(ConfigKey);
                else Persist(retained);
                Audit("config_reset", changed, username, role);
            });
            _overrides = retained;
            _current = ApplyOverrides(retained);
            return Task.CompletedTask;
        }
    }

    public Task UpdateAsync(NyaChatConfig config, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = JsonSerializer.SerializeToNode(config)!.AsObject();
            var overrides = new JsonObject();
            foreach (var spec in NyaChatHotSettings.All) overrides[spec.Key] = values[spec.Key]?.DeepClone();
            if (_overrides.TryGetPropertyValue(nameof(NyaChatConfig.Enabled), out var enabled))
                overrides[nameof(NyaChatConfig.Enabled)] = enabled?.DeepClone();
            _db.InTransaction(() => Persist(overrides));
            _overrides = overrides;
            _current = ApplyOverrides(overrides);
            return Task.CompletedTask;
        }
    }

    private NyaChatConfig ApplyOverrides(JsonObject overrides)
    {
        var node = JsonSerializer.SerializeToNode(_seed)!.AsObject();
        foreach (var (key, value) in overrides) node[key] = value?.DeepClone();
        return node.Deserialize<NyaChatConfig>()!;
    }

    private void Persist(JsonObject overrides)
    {
        if (_db.Get<NyaConfigEntry>(ConfigKey) != null) _db.Delete<NyaConfigEntry>(ConfigKey);
        _db.Insert(new NyaConfigEntry { ConfigKey = ConfigKey, ConfigJson = overrides.ToJsonString(), UpdatedAt = DateTime.UtcNow });
    }

    private void Audit(string action, string[] changed, string username, string role)
        => _db.Insert(new NyaAuditLog
        {
            LogId = Guid.NewGuid().ToString("N"), UserId = "__sys__", Action = action,
            DetailJson = JsonSerializer.Serialize(new { changed, @operator = new { username, role } }),
            Result = "success", CreatedAt = DateTime.UtcNow
        });

    private static NyaChatConfig Clone(NyaChatConfig config)
        => JsonSerializer.Deserialize<NyaChatConfig>(JsonSerializer.Serialize(config))!;
}
