using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.NyaChat;

public static class NyaLlmAdminSettings
{
    public static IReadOnlyList<string> CallSites { get; } = Array.AsReadOnly(new[]
    {
        NyaLlmTasks.Chat, NyaLlmTasks.Summarization, NyaLlmProfileStore.DefaultCallSite
    });

    public static string? Validate(NyaLlmProfileEntry entry, IEnumerable<INyaLlmProvider> providers)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(entry.ProfileId, @"^[A-Za-z0-9_\-]{1,80}$") || entry.ProfileId == NyaLlmProfileStore.DefaultCallSite)
            return "Profile ID 必須是 1–80 個英數字、_ 或 -，不可使用 __default__。";
        if (!providers.Any(p => p.Key.Equals(entry.Provider, StringComparison.OrdinalIgnoreCase)))
            return "Provider 未註冊。可用值：" + string.Join(", ", providers.Select(p => p.Key));
        if (!Uri.TryCreate(entry.BaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.AbsolutePath.Trim('/') == "" ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            return "Base URL 必須是完整的 http(s) API 端點，例如 openai_chat：http://localhost:1234/v1/chat/completions；ollama：http://localhost:11434/api/chat。";
        if (string.IsNullOrWhiteSpace(entry.Model) || entry.Model.Length > 120) return "Model 必須非空且不超過 120 個字元。";
        try
        {
            using var doc = JsonDocument.Parse(entry.ParamsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "params_json 必須是 JSON object。";
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var param in doc.RootElement.EnumerateObject())
            {
                if (!keys.Add(param.Name)) return $"重複參數：{param.Name}";
                if (param.Name == "extra_body")
                {
                    if (param.Value.ValueKind != JsonValueKind.Object) return "extra_body 必須是 JSON object。";
                    if (param.Value.EnumerateObject().Any(p => p.Name is "model" or "messages" or "tools" or "stream"))
                        return "extra_body 不可覆寫 model、messages、tools、stream。";
                    continue;
                }
                if (param.Name == "model_overrides")
                {
                    if (param.Value.ValueKind == JsonValueKind.Null) continue;
                    if (param.Value.ValueKind != JsonValueKind.Object) return "model_overrides 必須是數值參數 object。";
                    foreach (var value in param.Value.EnumerateObject())
                        if (value.Value.ValueKind != JsonValueKind.Number || !value.Value.TryGetDouble(out var n) || !double.IsFinite(n))
                            return $"model_overrides.{value.Name} 必須是有限數值。";
                    continue;
                }
                var spec = NyaLlmParamCatalog.All.FirstOrDefault(s => s.Key == param.Name);
                if (spec == null) return $"未知參數：{param.Name}。模型專屬參數請放在 model_overrides。";
                if (param.Value.ValueKind == JsonValueKind.Null && param.Name is "max_context_tokens" or "thinking_budget" or "enable_thinking") continue;
                if (spec.Type == "bool")
                {
                    if (param.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return $"{param.Name} 必須是 bool。";
                    continue;
                }
                if (param.Value.ValueKind != JsonValueKind.Number || !param.Value.TryGetDouble(out var number) || !double.IsFinite(number) ||
                    (spec.Min.HasValue && number < spec.Min) || (spec.Max.HasValue && number > spec.Max))
                    return $"{param.Name} 必須是範圍 [{spec.Min}, {spec.Max}] 內的有限數值。";
                if (param.Name is "max_context_tokens" or "timeout_seconds" or "thinking_budget" && !param.Value.TryGetInt32(out _))
                    return $"{param.Name} 必須是整數。";
            }
            _ = entry.ToProfile();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return "無效的 params_json：" + ex.Message;
        }
        return null;
    }

    public static string? ValidateExtraBody(string paramsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(paramsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return "params_json 必須是 JSON object。";
            if (!doc.RootElement.TryGetProperty("extra_body", out var extra)) return null;
            if (extra.ValueKind != JsonValueKind.Object) return "extra_body 必須是 JSON object。";
            if (extra.EnumerateObject().Any(p => p.Name is "model" or "messages" or "tools" or "stream"))
                return "extra_body 不可覆寫 model、messages、tools、stream。";
            return null;
        }
        catch (JsonException ex) { return ex.Message; }
    }

    public static JsonObject Sampling(NyaLlmProfile profile, NyaLlmRequest request)
    {
        var gemini = profile.Provider.Equals("gemini", StringComparison.OrdinalIgnoreCase);
        var sampling = new JsonObject
        {
            ["temperature"] = request.Temperature,
            [gemini ? "topP" : "top_p"] = request.TopP
        };
        if (!profile.SendTemperature) sampling.Remove("temperature");
        NyaLlmSerialization.ApplyModelOverrides(sampling, profile);
        return sampling;
    }

    public static object Effective(NyaLlmProfile profile)
    {
        var sampling = profile.Provider.Equals("openai_responses", StringComparison.OrdinalIgnoreCase)
            ? new JsonObject()
            : Sampling(profile, new NyaLlmRequest { Temperature = profile.Temperature, TopP = profile.TopP });
        var thinking = new JsonObject();
        if (profile.Provider is "openai_chat" or "openai_responses") NyaLlmSerialization.ApplyThinkingParams(thinking, profile);
        foreach (var param in thinking) sampling.Remove(param.Key);
        var replacedKeys = profile.Provider switch
        {
            "ollama" => new[] { "num_predict" },
            "gemini" => new[] { "maxOutputTokens" },
            "openai_responses" => new[] { "model", "input", "stream", "max_output_tokens" },
            "anthropic" => new[] { "model", "messages", "tools", "system", "max_tokens" },
            _ => new[] { "model", "messages", "tools", "stream", "max_tokens" }
        };
        foreach (var key in replacedKeys) sampling.Remove(key);
        return new
        {
            sampling,
            extra_body = profile.ExtraBody,
            sampling_location = profile.Provider == "ollama" ? "options" : profile.Provider == "gemini" ? "generationConfig" : "body",
            thinking,
            max_context_tokens = profile.MaxContextTokens,
            timeout_seconds = Math.Max(30, profile.TimeoutSeconds),
            output_limit = "每次請求由對話／摘要預算決定；連線測試使用 32 token。",
            precedence = "profile 取樣值 → model_overrides；每次請求的輸出上限及思考欄位再依 provider 套用。"
        };
    }
}
