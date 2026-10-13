using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 一筆工具的啟停狀態檢視（供 admin 介面）。
/// </summary>
public record NyaToolStatus(NyaToolSchema Schema, bool Enabled);

/// <summary>
/// NYA 工具登錄：總合工具 schema 建構、啟停管理與授權查詢。
/// </summary>
/// <remarks>
/// <para>
/// 登錄來源改為「<b>DI 發現的 <see cref="INyaToolPlugin"/> 集合 ∪ 啟停狀態 ∪ 授權</b>」，
/// 取代舊的 <c>AllTools</c>/<c>FactTools</c> 靜態清單與「外部工具只有 query 參數」限制。
/// 一個插件可暴露多個工具（<see cref="INyaToolPlugin.GetTools"/>），由本登錄攤平並建立
/// <c>toolName → plugin</c> 分派表供 Orchestrator 執行期查找。
/// </para>
/// <para>
/// 啟停由 <see cref="NyaToolEnableStore"/>（override map）決定；授權由 broker 提供的
/// <see cref="INyaToolAuthorizer"/> 決定。<see cref="BuildLlmTools"/> 是「暴露給 LLM」的第一道關卡，
/// Orchestrator 執行前再用 <see cref="TryGetPlugin"/> + authorizer 做第二道關卡。
/// </para>
/// </remarks>
public class NyaToolRegistry
{
    private readonly INyaToolAuthorizer _authorizer;
    private readonly NyaToolEnableStore _enableStore;
    private readonly ILogger<NyaToolRegistry> _logger;

    private readonly IReadOnlyList<NyaToolSchema> _allSchemas;
    private readonly IReadOnlyDictionary<string, (INyaToolPlugin Plugin, NyaToolSchema Schema)> _byToolName;

    public NyaToolRegistry(
        IEnumerable<INyaToolPlugin> plugins,
        INyaToolAuthorizer authorizer,
        NyaToolEnableStore enableStore,
        ILogger<NyaToolRegistry> logger)
    {
        _authorizer = authorizer;
        _enableStore = enableStore;
        _logger = logger;

        var schemas = new List<NyaToolSchema>();
        var map = new Dictionary<string, (INyaToolPlugin, NyaToolSchema)>(StringComparer.OrdinalIgnoreCase);

        // Bug #2（嚴審修復）：壞插件不得炸毀 broker（鐵則：Nya 是插件，不是 Broker 的心臟）。
        // 任一插件 GetTools() 拋例外 / 空名 / 撞名 → 記 log 並「跳過該插件或該工具」，絕不 throw。
        // 撞名採「先到先得」（first-wins），後者被跳過並記 warning。
        foreach (var plugin in plugins)
        {
            IReadOnlyList<NyaToolSchema>? pluginTools;
            try
            {
                pluginTools = plugin.GetTools();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[NyaToolRegistry] Plugin {Plugin} GetTools() threw; skipping entire plugin (broker stays up).",
                    plugin.GetType().Name);
                continue;
            }

            if (pluginTools is null)
            {
                _logger.LogWarning("[NyaToolRegistry] Plugin {Plugin} returned null from GetTools(); skipping.",
                    plugin.GetType().Name);
                continue;
            }

            foreach (var schema in pluginTools)
            {
                if (schema is null || string.IsNullOrWhiteSpace(schema.Name))
                {
                    _logger.LogWarning("[NyaToolRegistry] Plugin {Plugin} exposes a tool with empty/null Name; skipping.",
                        plugin.GetType().Name);
                    continue;
                }

                if (map.TryGetValue(schema.Name, out var existing))
                {
                    _logger.LogWarning(
                        "[NyaToolRegistry] Duplicate tool name '{Name}' from plugin {Plugin} " +
                        "(kept first from {Existing}); skipping duplicate.",
                        schema.Name, plugin.GetType().Name, existing.Item1.GetType().Name);
                    continue;
                }

                map[schema.Name] = (plugin, schema);
                schemas.Add(schema);
            }
        }

        _allSchemas = schemas;
        _byToolName = map;

        _logger.LogInformation(
            "[NyaToolRegistry] Discovered {ToolCount} tools from {PluginCount} plugins: {Names}",
            schemas.Count, _byToolName.Values.Select(v => v.Plugin).Distinct().Count(),
            string.Join(", ", schemas.Select(s => s.Name)));
    }

    // ── 啟停 / 狀態 ──────────────────────────────────────────────────────────

    /// <summary>某工具目前是否啟用（override ?? DefaultEnabled）。</summary>
    public bool IsEnabled(NyaToolSchema schema) => _enableStore.Effective(schema.Name, schema.DefaultEnabled);

    /// <summary>全部工具及其啟停狀態（供 admin 介面）。</summary>
    public IReadOnlyList<NyaToolStatus> GetAllWithStatus()
        => _allSchemas.Select(s => new NyaToolStatus(s, IsEnabled(s))).ToList();

    public bool IsKnownTool(string toolName) => _byToolName.ContainsKey(toolName);

    /// <summary>設定某工具啟停（D1：含事實工具）。</summary>
    public void SetEnabled(string toolName, bool enabled)
    {
        if (!IsKnownTool(toolName))
            throw new ArgumentException($"Unknown tool name: {toolName}");
        _enableStore.Set(toolName, enabled);
        _logger.LogInformation("[NyaToolRegistry] Tool {Name} {State}", toolName, enabled ? "enabled" : "disabled");
    }

    // ── LLM 工具清單建構（暴露面 = 啟用 ∩ 授權）─────────────────────────────

    /// <summary>
    /// 某使用者 / 通道目前「可用」的工具 schema（<b>啟用 ∩ 授權</b>）。
    /// 單一事實來源：供能力邊界動態生成（讀 <see cref="NyaToolSchema.CapabilityStatement"/>）使用；
    /// 這是「有效集合」（可操作的），不是「暴露給 LLM 的清單」（後者改為全暴露）。
    /// </summary>
    public IReadOnlyList<NyaToolSchema> GetUsableSchemas(string userId, string channelType)
        => _allSchemas.Where(s => IsEnabled(s) && _authorizer.CanUse(userId, channelType, s)).ToList();

    /// <summary>所有已註冊工具 schema（暴露面用；不過濾啟用/授權）。</summary>
    public IReadOnlyList<NyaToolSchema> GetAllSchemas() => _allSchemas;

    /// <summary>
    /// item 7：暴露閘默認放行（不拆閘，保留 seam，可日後收緊）。
    /// 理由：隱藏關閉/越權工具會讓 LLM 誤答「我沒有這功能」並寫進記憶；改為全暴露，
    /// 真正把關在執行門 <see cref="TryGetExecutablePlugin"/>（回結構化狀態）。
    /// </summary>
    private static bool IsExposedToLlm(NyaToolSchema schema) => true;

    /// <summary>建構暴露給 LLM 的工具清單：全暴露（啟用/授權的把關移至執行門）。</summary>
    public List<NyaLlmTool> BuildLlmTools(string userId, string channelType)
        => _allSchemas.Where(IsExposedToLlm)
            .Select(NyaToolSchemaMapper.ToLlmTool)
            .ToList();

    // ── 執行期查找 ───────────────────────────────────────────────────────────

    /// <summary>依工具名稱查找對應插件與 schema（供 Orchestrator 執行期分派）。</summary>
    public bool TryGetPlugin(string toolName, out INyaToolPlugin plugin, out NyaToolSchema schema)
    {
        if (_byToolName.TryGetValue(toolName, out var entry))
        {
            plugin = entry.Plugin;
            schema = entry.Schema;
            return true;
        }
        plugin = null!;
        schema = null!;
        return false;
    }

    /// <summary>執行前的授權再檢查（第二道關卡，防止 LLM 繞過暴露面）。</summary>
    public bool CanUse(string userId, string channelType, NyaToolSchema schema)
        => _authorizer.CanUse(userId, channelType, schema);

    /// <summary>
    /// 執行門（Bug #8 嚴審修復）：把「已知 ∩ 已啟用 ∩ 授權」三道關卡收斂為單一執行前檢查。
    /// item 7：denyStatus 改為結構化狀態碼（<c>unknown_tool</c> / <c>disabled</c> / <c>not_authorized</c>），
    /// 方便 Orchestrator 依狀態給使用者差異化回覆，而不是解析自由文字。
    /// </summary>
    /// <param name="denyStatus">未通過時填入結構化狀態碼：<c>unknown_tool</c>、<c>disabled</c>、<c>not_authorized</c>；通過時為 null。</param>
    public bool TryGetExecutablePlugin(
        string toolName, string userId, string channelType,
        out INyaToolPlugin plugin, out NyaToolSchema schema, out string? denyStatus)
    {
        plugin = null!;
        schema = null!;

        if (!_byToolName.TryGetValue(toolName, out var entry))
        {
            denyStatus = "unknown_tool";
            return false;
        }

        if (!IsEnabled(entry.Schema))
        {
            denyStatus = "disabled";
            return false;
        }

        if (!_authorizer.CanUse(userId, channelType, entry.Schema))
        {
            denyStatus = "not_authorized";
            return false;
        }

        plugin = entry.Plugin;
        schema = entry.Schema;
        denyStatus = null;
        return true;
    }
}
