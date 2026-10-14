using System.Text.Json;
using BaseOrm;

namespace Broker.NyaChat;

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

        [Column("params_json")] public string ParamsJson { get; set; } = "{}";

        [Column("template")] [MaxLength(40)] public string Template { get; set; } = "";

    [Column("created_at")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [Column("updated_at")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        private sealed class ParamBlob
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public float? temperature { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public System.Text.Json.Nodes.JsonObject? extra_body { get; set; }
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
            EnableThinking = b.enable_thinking, ThinkingBudget = b.thinking_budget, ExtraBody = b.extra_body,
            SendTemperature = doc.RootElement.TryGetProperty("temperature", out var temp) && temp.ValueKind != JsonValueKind.Null
        };
    }

    public static NyaLlmProfileEntry FromProfile(string profileId, string template, NyaLlmProfile p)
        => new()
        {
            ProfileId = profileId, Provider = p.Provider, BaseUrl = p.BaseUrl,
            ApiKey = p.ApiKey, Model = p.Model, Template = template,
            ParamsJson = JsonSerializer.Serialize(new ParamBlob
            {
                temperature = p.SendTemperature ? p.Temperature : null, extra_body = p.ExtraBody, top_p = p.TopP, timeout_seconds = p.TimeoutSeconds,
                max_context_tokens = p.MaxContextTokens, model_overrides = p.ModelOverrides?.ToDictionary(kv => kv.Key, kv => (double?)kv.Value),
                enable_thinking = p.EnableThinking, thinking_budget = p.ThinkingBudget
            })
        };
}

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
