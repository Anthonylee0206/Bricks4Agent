using System.Text.Json;
using BaseOrm;

namespace Broker.NyaChat;

/// <summary>
/// Doc 3 Plan A：profile 持久化行。transport 欄位（provider/base_url/api_key/model）為頂層欄；
/// 取樣 / thinking / 覆寫等參數打包進 <see cref="ParamsJson"/>（JSON 為準，常見＋罕見同處）。
/// </summary>
[Table("nya_llm_profiles")]
public class NyaLlmProfileEntry
{
    [Key(AutoIncrement = false)]
    [Column("profile_id")]
    [MaxLength(80)]
    public string ProfileId { get; set; } = "";

    [Column("provider")]  [MaxLength(40)] public string Provider { get; set; } = "ollama";
    [Column("base_url")]                  public string BaseUrl  { get; set; } = "";
    [Column("api_key")]                   public string ApiKey   { get; set; } = "";
    [Column("model")]     [MaxLength(120)] public string Model   { get; set; } = "";

    /// <summary>取樣/thinking/覆寫等參數的 JSON blob（見 ToProfile/FromProfile）。</summary>
    [Column("params_json")] public string ParamsJson { get; set; } = "{}";

    /// <summary>建立來源家族（chatgpt/local/claude/gemini），參考用。</summary>
    [Column("template")] [MaxLength(40)] public string Template { get; set; } = "";

    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>params_json 的型別化形狀（序列化/反序列化用）。</summary>
    private sealed class ParamBlob
    {
        public float? temperature { get; set; } = 0.7f;
        public float? top_p { get; set; } = 0.9f;
        public int? timeout_seconds { get; set; } = 120;
        public int? max_context_tokens { get; set; }
        public Dictionary<string, double?>? model_overrides { get; set; }
        public bool? enable_thinking { get; set; }
        public int? thinking_budget { get; set; }
    }

    public NyaLlmProfile ToProfile()
    {
        var b = JsonSerializer.Deserialize<ParamBlob>(
            string.IsNullOrWhiteSpace(ParamsJson) ? "{}" : ParamsJson) ?? new ParamBlob();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(ParamsJson) ? "{}" : ParamsJson);
        var overrides = new Dictionary<string, double>();
        foreach (var spec in NyaLlmParamCatalog.All.Where(s => s.Key is "frequency_penalty" or "presence_penalty" or "repeat_penalty"))
            if (doc.RootElement.TryGetProperty(spec.Key, out var value) && value.ValueKind == JsonValueKind.Number) overrides[spec.Key] = value.GetDouble();
        if (b.model_overrides != null)
            foreach (var (key, value) in b.model_overrides) if (value.HasValue) overrides[key] = value.Value;
        return new NyaLlmProfile
        {
            ProfileId = ProfileId,
            Provider = Provider, BaseUrl = BaseUrl, ApiKey = ApiKey, Model = Model,
            Temperature = b.temperature ?? 0.7f, TopP = b.top_p ?? 0.9f, TimeoutSeconds = b.timeout_seconds ?? 120,
            MaxContextTokens = b.max_context_tokens, ModelOverrides = overrides.Count == 0 ? null : overrides,
            EnableThinking = b.enable_thinking, ThinkingBudget = b.thinking_budget
        };
    }

    public static NyaLlmProfileEntry FromProfile(string profileId, string template, NyaLlmProfile p)
        => new()
        {
            ProfileId = profileId, Provider = p.Provider, BaseUrl = p.BaseUrl,
            ApiKey = p.ApiKey, Model = p.Model, Template = template,
            ParamsJson = JsonSerializer.Serialize(new ParamBlob
            {
                temperature = p.Temperature, top_p = p.TopP, timeout_seconds = p.TimeoutSeconds,
                max_context_tokens = p.MaxContextTokens, model_overrides = p.ModelOverrides?.ToDictionary(kv => kv.Key, kv => (double?)kv.Value),
                enable_thinking = p.EnableThinking, thinking_budget = p.ThinkingBudget
            })
        };
}

/// <summary>Doc 3 Plan A：呼叫點 → profile 路由行。call_site_id="__default__" 為 fallback。</summary>
[Table("nya_llm_routes")]
public class NyaLlmRoute
{
    [Key(AutoIncrement = false)]
    [Column("call_site_id")]
    [MaxLength(80)]
    public string CallSiteId { get; set; } = "";

    [Column("profile_id")] [MaxLength(80)] public string ProfileId { get; set; } = "";
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
