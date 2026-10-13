using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 一份具名 prompt 模板，以 JSON 外部化，取代散落在 C# 的硬編碼字串。
/// </summary>
/// <remarks>
/// <see cref="Sections"/> 為單/多行片段，<see cref="Lists"/> 為條列項。
/// 片段內以 <c>{key}</c> 佔位符由呼叫端 Render 填充（如 <c>{display_name}</c> / <c>{current_datetime}</c>）。
/// </remarks>
public sealed class NyaPromptTemplate
{
    public string Id { get; init; } = "";

    /// <summary>模板版本（D4 擴展點：未來 A/B 以此選版，本項不實作分流）。</summary>
    public int Version { get; init; } = 1;

    public Dictionary<string, string> Sections { get; init; } = new();
    public Dictionary<string, List<string>> Lists { get; init; } = new();
}

/// <summary>
/// Prompt 模板提供者。比照 <see cref="NyaSoulProvider"/> 的
/// 「JSON 檔 + FileSystemWatcher 熱重載 + 缺檔 fallback」範式。
/// </summary>
/// <remarks>
/// <para>
/// <b>D3 韌性（守鐵則）</b>：內建一份等價現行文字的預設模板。檔案以「<b>覆蓋</b>」語意載入——
/// 檔案中出現的 section/list 覆寫預設，未出現者沿用內建。即使 <c>prompts/</c> 目錄遺失或
/// JSON 損毀，<see cref="Section"/> / <see cref="List"/> 仍回傳可用文字，PromptBuilder 不致崩潰。
/// </para>
/// <para>
/// 消費端：<see cref="NyaPromptBuilder"/>（system prompt）、<see cref="NyaFactExtractor"/>（抽取）、
/// <see cref="NyaSummarizer"/>（摘要）。動態資料（facts/對話內容）仍由程式拼接，只有固定指令文字外部化。
/// </para>
/// </remarks>
public sealed class NyaPromptTemplateProvider : IDisposable
{
    private readonly INyaConfigStore<NyaChatConfig> _configStore;
    private NyaChatConfig _config => _configStore.Current; // 當前配置快照
    private readonly ILogger<NyaPromptTemplateProvider> _logger;

    private readonly object _cacheLock = new();
    private Dictionary<string, NyaPromptTemplate> _cache = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public NyaPromptTemplateProvider(
        INyaConfigStore<NyaChatConfig> configStore,
        ILogger<NyaPromptTemplateProvider> logger)
    {
        _configStore = configStore;
        _logger = logger;
        LoadAll();
        SetupWatcher();
    }

    /// <summary>取得某 section 文字並以 <paramref name="vars"/> 填充 <c>{key}</c> 佔位符；缺則回 fallback。</summary>
    public string Section(string templateId, string sectionKey, IReadOnlyDictionary<string, string>? vars = null)
    {
        string? text;
        lock (_cacheLock)
            text = _cache.TryGetValue(templateId, out var t) && t.Sections.TryGetValue(sectionKey, out var s) ? s : null;

        text ??= FallbackSection(templateId, sectionKey);
        return vars is { Count: > 0 } ? Render(text, vars) : text;
    }

    /// <summary>取得某 list（條列項）；缺則回內建 fallback。</summary>
    public IReadOnlyList<string> List(string templateId, string listKey)
    {
        lock (_cacheLock)
            if (_cache.TryGetValue(templateId, out var t) && t.Lists.TryGetValue(listKey, out var l) && l.Count > 0)
                return l;
        return FallbackList(templateId, listKey);
    }

    /// <summary>列出目前載入的模板 id（供 admin 檢視）。</summary>
    public IReadOnlyList<(string Id, int Version)> ListTemplates()
    {
        lock (_cacheLock)
            return _cache.Values.Select(t => (t.Id, t.Version)).ToList();
    }

    /// <summary>佔位符替換：將 <c>{key}</c> 換成對應值（簡單字串替換，不引入模板引擎）。</summary>
    public static string Render(string text, IReadOnlyDictionary<string, string> vars)
    {
        if (string.IsNullOrEmpty(text) || vars.Count == 0) return text;
        var sb = new StringBuilder(text);
        foreach (var (k, v) in vars)
            sb.Replace("{" + k + "}", v ?? "");
        return sb.ToString();
    }

    /// <summary>強制重新載入所有模板（熱更新 / admin 觸發）。</summary>
    public void Reload() => LoadAll();

    public void Dispose() => _watcher?.Dispose();

    // ── 內部 ────────────────────────────────────────────────────────────────

    private string GetPromptsDir()
    {
        var baseDir = AppContext.BaseDirectory;
        return Path.IsPathRooted(_config.PromptsDirectory)
            ? _config.PromptsDirectory
            : Path.Combine(baseDir, _config.PromptsDirectory);
    }

    private void LoadAll()
    {
        // 以內建預設為基底，檔案內容覆蓋其上（D3：部分檔案只覆寫指定 section/list）。
        var merged = BuiltInDefaults().ToDictionary(kv => kv.Key, kv => Clone(kv.Value), StringComparer.OrdinalIgnoreCase);

        try
        {
            var dir = GetPromptsDir();
            if (Directory.Exists(dir))
            {
                foreach (var file in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var loaded = JsonSerializer.Deserialize<NyaPromptTemplate>(json, JsonOpts);
                        var id = !string.IsNullOrWhiteSpace(loaded?.Id)
                            ? loaded!.Id
                            : Path.GetFileNameWithoutExtension(file);
                        if (loaded == null || string.IsNullOrWhiteSpace(id)) continue;

                        if (!merged.TryGetValue(id, out var target))
                        {
                            target = new NyaPromptTemplate { Id = id, Version = loaded.Version };
                            merged[id] = target;
                        }
                        foreach (var (k, v) in loaded.Sections) target.Sections[k] = v;   // 覆蓋
                        foreach (var (k, v) in loaded.Lists) target.Lists[k] = v;         // 覆蓋
                        _logger.LogInformation("[NyaPrompt] Loaded template '{Id}' (v{Ver}) from {File}", id, loaded.Version, Path.GetFileName(file));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[NyaPrompt] Failed to load template file {File}; using built-in for its keys.", file);
                    }
                }
            }
            else
            {
                _logger.LogWarning("[NyaPrompt] Prompts directory not found: {Dir}. Using built-in defaults.", dir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaPrompt] Failed to scan prompts directory; using built-in defaults.");
        }

        lock (_cacheLock) _cache = merged;
    }

    private void SetupWatcher()
    {
        try
        {
            var dir = GetPromptsDir();
            if (!Directory.Exists(dir)) return;

            _watcher = new FileSystemWatcher(dir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
                IncludeSubdirectories = false
            };

            Timer? debounce = null;
            void OnChange(object _, FileSystemEventArgs __)
            {
                debounce?.Dispose();
                debounce = new Timer(_ =>
                {
                    _logger.LogInformation("[NyaPrompt] Template change detected, reloading…");
                    Reload();
                }, null, 400, Timeout.Infinite);
            }

            _watcher.Changed += OnChange;
            _watcher.Created += OnChange;
            _watcher.Deleted += OnChange;
            _watcher.Renamed += (s, e) => OnChange(s, e);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[NyaPrompt] Failed to setup template file watcher");
        }
    }

    private static NyaPromptTemplate Clone(NyaPromptTemplate t) => new()
    {
        Id = t.Id,
        Version = t.Version,
        Sections = new Dictionary<string, string>(t.Sections),
        Lists = t.Lists.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value))
    };

    private string FallbackSection(string templateId, string sectionKey)
    {
        if (BuiltInDefaults().TryGetValue(templateId, out var t) && t.Sections.TryGetValue(sectionKey, out var s))
            return s;
        _logger.LogDebug("[NyaPrompt] No template/built-in for {Id}.{Key}; returning empty.", templateId, sectionKey);
        return "";
    }

    private IReadOnlyList<string> FallbackList(string templateId, string listKey)
    {
        if (BuiltInDefaults().TryGetValue(templateId, out var t) && t.Lists.TryGetValue(listKey, out var l))
            return l;
        return Array.Empty<string>();
    }

    // ── 內建預設（等價現行硬編碼文字；缺檔/壞檔時的最終 fallback，守鐵則）──────────
    // 與 Prompt/prompts/*.json 內容保持一致；JSON 檔可逐 section 覆寫。

    private static Dictionary<string, NyaPromptTemplate>? _builtins;
    private static Dictionary<string, NyaPromptTemplate> BuiltInDefaults()
    {
        return _builtins ??= new(StringComparer.OrdinalIgnoreCase)
        {
            ["system"] = new NyaPromptTemplate
            {
                Id = "system",
                Sections = new()
                {
                    ["identity_name"]     = "你的名字是 {display_name}。",
                    ["identity_tone"]     = "語氣風格：{tone}",
                    ["identity_language"] = "使用語言：{language}",
                    ["identity_greeting"] = "打招呼方式：{greeting_style}",
                    ["identity_humor"]    = "幽默程度：{humor_level}",
                    ["identity_formality"]= "正式程度：{formality}",
                    ["behavior_rules_header"] = "行為規則：",
                    ["forbidden_header"]      = "禁止事項：",
                    // 5F：原 NyaPromptBuilder L261 的英文行，移此並中文化、統一語言
                    ["behavior_directive"] = "請直接、簡潔地回答，不要逐步說明你的思考過程或逐步推理。",
                    ["capabilities_can_header"]   = "你目前具備以下能力：",
                    ["capabilities_limit_header"] = "以下是你的能力邊界，請誠實說明（不要假裝可以做到）：",
                    // 5C：記憶操作指引（行為導引，非工具 schema；不重複 function-calling 描述）
                    ["memory_guidance"] = "記憶操作指引：\n- 主動記住使用者提到的重要資訊（姓名、偏好、待辦事項等）\n- 不需要告訴使用者你正在呼叫工具，自然地完成後確認即可\n- 若資訊已失效主動移除，若有新資訊主動更新",
                    // 5D：時間感知
                    ["instructions_time"] = "現在時間：{current_datetime}",
                    ["facts_intro"]    = "以下是你記住的使用者資訊（長期記憶，跨對話持久保存）：",
                    ["facts_guidance"] = "請主動利用這些記憶回覆。若資訊已失效，用 delete_fact 移除；若有新資訊，用 upsert_fact 更新。",
                    ["summaries_intro"] = "以下是過去對話的摘要（長期記憶，供你理解脈絡）：",
                    // Doc 2：任務層程序性收尾（三層之末、最明確）。
                    // 防幻覺：沒有對應工具執行結果卻聲稱用過工具 = 胡言亂語。
                    ["task_closing"] =
                        "你現在要做的：根據以上人格與記憶，回應使用者的最新訊息。需要外部資料或執行動作時，呼叫對應的工具；不要假裝呼叫或捏造工具結果。只有實際出現工具執行結果時，才能向使用者回報你用了某個工具或做了某個動作；沒有對應的執行結果卻聲稱用過工具，等同胡言亂語。當認為執行工具所需的資訊已足夠並且要求明確的情況下，立即停止回覆並呼叫 tool。\n" +
                        "輸出：直接、簡潔的繁體中文回覆給使用者，不要展示思考過程、推理步驟或工具呼叫細節。",
                    // Doc 2 surface #2：閉環二次回覆——用人格轉述工具結果、勿改數字/事實。
                    // 新增：每筆結果前已標註 [工具執行狀態：成功/失敗/未啟動]，並要求逐一核對「有無執行結果」防幻覺。
                    ["tool_result_personalize"] =
                        "以下是工具執行的結果。每一筆都以「[工具執行狀態：成功/失敗/未啟動]」開頭，標示該工具實際的執行狀態。請用你的人格，把結果自然地轉述給使用者：\n" +
                        "- 先看每一筆的執行狀態：『成功』才可當作已完成的事實轉述；『失敗』要如實說明沒做到與原因；『未啟動』代表該功能關閉、無權限或不存在、根本沒有執行，要照實告知、不要假裝有做。\n" +
                        "- 不要更改任何數字、名稱、時間或事實，也不要捏造工具沒有提供的資訊。\n" +
                        "- 逐一核對：你打算告訴使用者的每個工具動作，上文是否真的有對應的工具執行結果。若你想說某個工具做了什麼，但上文根本沒有它的執行結果，那就是憑空捏造（胡言亂語）——絕對不要輸出。\n" +
                        "- 直接輸出給使用者看的繁體中文回覆，不要展示工具細節或思考過程。",
                },
                Lists = new()
                {
                    // 5E：僅保留「真正全域不變」的限制；會被插件推翻者（如交易/付款）已移除，改由工具能力動態表述
                    ["global_limits"] = new()
                    {
                        "我無法主動傳送訊息，只能回覆你發起的對話",
                        "我無法設定定時提醒、鬧鐘或排程任務",
                        "我無法存取你的裝置、相簿或本地檔案",
                    }
                }
            },
            ["fact_extraction"] = new NyaPromptTemplate
            {
                Id = "fact_extraction",
                Sections = new()
                {
                    ["instruction"] =
                        "你是使用者長期記憶的「維護員」。給你目前已記住的事實清單與最近對話，產出一份維護操作清單，讓記憶保持正確、不過時。\n" +
                        "你可以做三種操作：\n" +
                        "- add：對話出現值得長期記住的新事實（身份、偏好、進行中的任務、使用者明確要你記住的事）。\n" +
                        "- update：既有事實的內容變了，用同一個 key 提供新值。\n" +
                        "- retire：既有事實已過時或不再成立（任務完成、偏好改變），把它退役。retire 要謹慎——只在對話清楚顯示它不再成立時才做，並給高 confidence。\n" +
                        "規則：\n" +
                        "- 只輸出有把握的維護操作；沒有就回空陣列 []。\n" +
                        "- confidence 0.0~1.0：明說的給 0.9+，推測的給 0.6~0.8；retire 需 0.85 以上。\n" +
                        "- 不要 retire『使用者明確要你記住』(instruction 類) 的事實。\n" +
                        "- 這些事實會被「跨所有話題、長期持久」地帶進使用者之後的每一段對話；寧缺勿錯——只記真正穩定、跨情境都成立的事實，別把某個話題裡的一時內容或臆測當成長期事實。\n" +
                        "- 每筆都要附簡短 reason。",
                    ["categories"] =
                        "category：\n" +
                        "  identity   - 姓名、職業、年齡等身份\n" +
                        "  preference - 語氣偏好、習慣、喜好、在意的人事物\n" +
                        "  context    - 進行中的專案/任務/近況\n" +
                        "  instruction - 使用者明確要求「記住某事」（不可自動 retire）",
                    ["existing_facts_intro"] = "（以下是目前已記住的事實；針對它們判斷有沒有要 update / retire，並從對話找新的 add）",
                    ["output_format"] =
                        "只回覆 JSON array，不要任何說明文字。每個元素：\n" +
                        "{\"op\":\"add|update|retire\",\"category\":\"...\",\"key\":\"英文底線key\",\"value\":\"內容(retire可省)\",\"confidence\":0.0~1.0,\"reason\":\"簡短原因\"}\n" +
                        "範例：[{\"op\":\"update\",\"category\":\"context\",\"key\":\"current_project\",\"value\":\"改用 Rust 重寫\",\"confidence\":0.9,\"reason\":\"從 Python 改 Rust\"}]",
                }
            },
            ["summarization"] = new NyaPromptTemplate
            {
                Id = "summarization",
                Sections = new()
                {
                    ["intro_merge"]  = "你是對話摘要引擎。以下有一份先前累積的摘要，以及一批新的對話記錄。請合併兩者，輸出一份更新後的完整摘要，仍以「這段對話實際發生了什麼」為主體，供後續對話回顧。",
                    ["intro_fresh"]  = "你是對話摘要引擎。請閱讀以下使用者與助理的對話，寫出一份摘要，記錄這段對話實際發生了什麼、談了哪些事、得到什麼結論，供後續對話回顧。",
                    ["requirements"] =
                        "要求：\n" +
                        "- 摘要主體是「這段對話實際發生了什麼」：談的主題、提的問題、做的決定、完成或待辦的事。\n" +
                        "- 同時記錄『情感與關係性』：使用者當下的情緒、聊到的玩笑或內梗、他在意或煩惱的事、氣氛——這是陪伴體驗的核心，要保留，別洗成乾巴巴的條目。\n" +
                        "- 以回顧的口吻、客觀第三人稱記錄（不要用第一人稱角色口吻重演對話）。\n" +
                        "- 仍要略過這些非對話內容：話題管理操作（命名/建立/切換/刪除話題）、詢問助理能力的問答與助理功能/限制清單、純確認語（『好的』『收到』）、工具執行細節與系統訊息。\n" +
                        "- 不要記錄使用者的個人事實/偏好（姓名、喜好）——那由事實系統負責，摘要不重複。\n" +
                        "- 若某段只有上述要略過的內容、沒有實質，略過不記。\n" +
                        "- 條列式、繁體中文，每點一件事；長度隨內容、完整捕捉實質與情感弧，不要硬性壓縮。",
                    ["closing"] = "請直接輸出合併後的完整摘要，不需要任何前言或說明：",
                }
            },
        };
    }
}
