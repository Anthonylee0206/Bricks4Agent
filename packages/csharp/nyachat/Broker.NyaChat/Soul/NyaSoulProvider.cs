using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// Soul 人格系統：從 JSON 定義檔載入人格設定，支援對不同使用者綁定不同 Soul。
/// Soul 綁定存儲於獨立的 nya_soul_bindings 表，與事實系統完全分離。
/// </summary>
public class NyaSoulProvider : IDisposable
{
    private readonly INyaConfigStore<NyaChatConfig> _configStore;
    private NyaChatConfig _config => _configStore.Current; // 當前配置快照
    private readonly NyaMemoryService _memoryService;
    private readonly ILogger<NyaSoulProvider> _logger;

    private readonly Dictionary<string, SoulDefinition> _soulCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _soulFilePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private FileSystemWatcher? _watcher;

    public NyaSoulProvider(
        INyaConfigStore<NyaChatConfig> configStore,
        NyaMemoryService memoryService,
        ILogger<NyaSoulProvider> logger)
    {
        _configStore = configStore;
        _memoryService = memoryService;
        _logger = logger;
        LoadAllSouls();
        SetupWatcher();
    }

    /// <summary>
    /// 取得使用者對應的 Soul。
    /// 優先查 nya_soul_bindings；若無則使用 DefaultSoulId。
    /// </summary>
    public SoulDefinition GetSoul(string userId)
    {
        var soulId = _memoryService.GetSoulBinding(userId) ?? _config.DefaultSoulId;
        return GetSoulById(soulId) ?? GetSoulById(_config.DefaultSoulId) ?? CreateFallbackSoul();
    }

    /// <summary>列出所有可用 Soul 的 ID 與顯示名稱</summary>
    public IReadOnlyList<(string Id, string DisplayName)> ListSouls()
    {
        lock (_cacheLock)
        {
            return _soulCache.Values
                .Select(s => (s.SoulId, s.DisplayName))
                .ToList();
        }
    }

    /// <summary>取得單一 Soul 完整定義（API 編輯用）</summary>
    public SoulDefinition? GetSoulDefinition(string soulId)
    {
        lock (_cacheLock)
            return _soulCache.TryGetValue(soulId, out var soul) ? soul : null;
    }

    /// <summary>儲存 Soul 定義到 JSON 檔案並更新快取（新建 / 更新皆可）</summary>
    public void SaveSoul(SoulDefinition soul)
    {
        var soulsDir = GetSoulsDir();
        if (!Directory.Exists(soulsDir))
            Directory.CreateDirectory(soulsDir);

        var filePath = Path.Combine(soulsDir, $"{soul.SoulId}.json");
        var json = JsonSerializer.Serialize(soul, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        });
        File.WriteAllText(filePath, json);

        lock (_cacheLock)
        {
            _soulCache[soul.SoulId] = soul;
            _soulFilePaths[soul.SoulId] = filePath;
        }
        _logger.LogInformation("[NyaSoul] Saved soul: {Id} ({Name}) → {File}", soul.SoulId, soul.DisplayName, filePath);
    }

    /// <summary>刪除 Soul 定義（檔案 + 快取）。若 Soul 不存在回傳 false。</summary>
    public bool DeleteSoul(string soulId)
    {
        string? filePath;
        lock (_cacheLock)
        {
            _soulCache.Remove(soulId);
            _soulFilePaths.TryGetValue(soulId, out filePath);
            _soulFilePaths.Remove(soulId);
        }

        if (filePath != null && File.Exists(filePath))
        {
            File.Delete(filePath);
            _logger.LogInformation("[NyaSoul] Deleted soul: {Id}", soulId);
            return true;
        }

        // fallback: try conventional path
        var soulsDir = GetSoulsDir();
        var conventional = Path.Combine(soulsDir, $"{soulId}.json");
        if (File.Exists(conventional))
        {
            File.Delete(conventional);
            _logger.LogInformation("[NyaSoul] Deleted soul: {Id}", soulId);
            return true;
        }

        return false;
    }

    /// <summary>強制重新載入所有 Soul 定義（Runtime 熱更新用）</summary>
    public void ReloadSouls()
    {
        lock (_cacheLock)
        {
            _soulCache.Clear();
            _soulFilePaths.Clear();
        }
        LoadAllSouls();
    }

    public void Dispose() => _watcher?.Dispose();

    // ── 內部 ────────────────────────────────────────────────────────────────

    private string GetSoulsDir()
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.IsPathRooted(_config.SoulsDirectory)
            ? _config.SoulsDirectory
            : Path.Combine(baseDir, _config.SoulsDirectory);
    }

    private void SetupWatcher()
    {
        try
        {
            var soulsDir = GetSoulsDir();

            if (!Directory.Exists(soulsDir)) return;

            _watcher = new FileSystemWatcher(soulsDir, "*.json")
            {
                NotifyFilter      = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };

            // 防抖：短時間內多次觸發只重載一次
            Timer? debounce = null;
            void OnChange(object _, FileSystemEventArgs __)
            {
                debounce?.Dispose();
                debounce = new Timer(_ =>
                {
                    _logger.LogInformation("[NyaSoul] File change detected, reloading souls…");
                    ReloadSouls();
                }, null, 400, Timeout.Infinite);
            }

            _watcher.Changed += OnChange;
            _watcher.Created += OnChange;
            _watcher.Deleted += OnChange;
            _watcher.Renamed += (s, e) => OnChange(s, e);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaSoul] Failed to setup file watcher");
        }
    }

    private SoulDefinition? GetSoulById(string soulId)
    {
        lock (_cacheLock)
        {
            return _soulCache.TryGetValue(soulId, out var soul) ? soul : null;
        }
    }

    private void LoadAllSouls()
    {
        try
        {
            var soulsDir = GetSoulsDir();

            if (!Directory.Exists(soulsDir))
            {
                _logger.LogWarning("[NyaSoul] Souls directory not found: {Dir}. Using fallback soul.", soulsDir);
                return;
            }

            var files = Directory.GetFiles(soulsDir, "*.json", SearchOption.TopDirectoryOnly);
            lock (_cacheLock)
            {
                foreach (var file in files)
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var soul = JsonSerializer.Deserialize<SoulDefinition>(json,
                            new JsonSerializerOptions
                            {
                                PropertyNameCaseInsensitive = true,
                                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                            });

                        if (soul != null && !string.IsNullOrWhiteSpace(soul.SoulId))
                        {
                            _soulCache[soul.SoulId] = soul;
                            _soulFilePaths[soul.SoulId] = file;
                            _logger.LogInformation("[NyaSoul] Loaded soul: {Id} ({Name})",
                                soul.SoulId, soul.DisplayName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[NyaSoul] Failed to load soul file: {File}", file);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaSoul] Failed to scan souls directory");
        }
    }

    private static SoulDefinition CreateFallbackSoul() => new()
    {
        SoulId = "fallback",
        DisplayName = "NYA",
        Personality = new SoulPersonality
        {
            Tone = "友善、直接",
            Language = "繁體中文",
            GreetingStyle = "自然",
            HumorLevel = "適度",
            Formality = "半正式"
        },
        Rules = new List<string>
        {
            "回答要直接切入重點",
            "不知道的事情要誠實說不知道"
        },
        Forbidden = new List<string>
        {
            "不可捏造系統不存在的功能"
        }
    };
}
