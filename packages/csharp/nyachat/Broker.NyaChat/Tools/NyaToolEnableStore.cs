using System.Text.Json;

namespace Broker.NyaChat;

/// <summary>
/// 工具啟停狀態的持久化。從 <c>NyaToolRegistry</c> 抽離，使登錄層與 store 層責任分離。
/// </summary>
/// <remarks>
/// <para>
/// 語意升級為 <b>override map</b>：<c>{ "search": true, "upsert_fact": false }</c>。
/// 每個工具的有效啟用值 = <c>override ?? schema.DefaultEnabled</c>，故能同時支援
/// 預設開啟的事實工具（可被個別關閉，D1）與預設關閉的外部工具。
/// </para>
/// <para>
/// <b>向後相容</b>：偵測舊格式（<c>["search","rail"]</c> JSON 陣列）時，視為「這些 = 啟用」的集合，
/// 其餘工具沿用各自 <c>DefaultEnabled</c>；首次 <see cref="Save"/> 自動改寫為新 override map 格式。
/// </para>
/// 路徑沿用：<c>NyaChat:DataDirectory</c>，否則 <c>AppContext.BaseDirectory</c>，檔名 <c>nya_tools.json</c>。
/// </remarks>
public sealed class NyaToolEnableStore
{
    private readonly string _configPath;
    private readonly object _lock = new();
    private readonly ILogger<NyaToolEnableStore> _logger;
    private Dictionary<string, bool> _overrides;

    public NyaToolEnableStore(IConfiguration configuration, ILogger<NyaToolEnableStore> logger)
    {
        _logger = logger;
        var dataDir = configuration["NyaChat:DataDirectory"] ?? AppContext.BaseDirectory;
        _configPath = Path.Combine(dataDir, "nya_tools.json");
        _overrides  = Load();
    }

    /// <summary>有效啟用值：override 優先，否則回退至 schema 預設。</summary>
    public bool Effective(string toolName, bool defaultEnabled)
    {
        lock (_lock)
            return _overrides.TryGetValue(toolName, out var v) ? v : defaultEnabled;
    }

    /// <summary>設定某工具的明確啟停 override 並持久化。</summary>
    public void Set(string toolName, bool enabled)
    {
        lock (_lock)
        {
            _overrides[toolName] = enabled;
            Save();
        }
    }

    // ── 持久化 ───────────────────────────────────────────────────────────────

    private Dictionary<string, bool> Load()
    {
        try
        {
            if (!File.Exists(_configPath))
                return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            var json = File.ReadAllText(_configPath);
            using var doc = JsonDocument.Parse(json);

            // 新格式：物件 { name: bool }
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (prop.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        map[prop.Name] = prop.Value.GetBoolean();
                return map;
            }

            // 舊格式：陣列 ["search","rail"] → 視為「這些 = 啟用」
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in doc.RootElement.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        map[item.GetString()!] = true;
                _logger.LogInformation("[NyaToolEnableStore] Migrated legacy array format ({Count} enabled tools).", map.Count);
                return map;
            }

            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaToolEnableStore] Failed to load from {Path}", _configPath);
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_overrides, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaToolEnableStore] Failed to save to {Path}", _configPath);
        }
    }
}
