using System.Text.Json;
using System.Text.Json.Nodes;

namespace Broker.NyaChat;

public sealed record NyaChatSettingSpec(string Key, string Label, string Type, double? Min, double? Max, string Description);

public static class NyaChatHotSettings
{
    public static readonly IReadOnlyList<NyaChatSettingSpec> All = new[]
    {
        new NyaChatSettingSpec("NoteCapacity", "有效筆記容量", "integer", 1, 10000, "所有有效筆記每輪完整帶入；新增與還原受此上限限制。"),
        new NyaChatSettingSpec("NoteMaxLength", "單則筆記字數", "integer", 1, 100000, "超過時回報筆記已滿，請先整理。"),
        new NyaChatSettingSpec("TopicCapacity", "聊天室容量", "integer", 1, 10000, "完整聊天室目錄的容量上限。"),
        new NyaChatSettingSpec("ShortTermMessageLimit", "目前聊天室的近期訊息上限", "integer", 1, 100000, "僅目前聊天室，排除遺忘內容；實際帶入數再受 token 預算限制。"),
        new NyaChatSettingSpec("DefaultMaxContextTokens", "預設上下文 token", "integer", 512, 2000000, "僅在 chat profile 未指定最大上下文 token 時使用。"),
        new NyaChatSettingSpec("OutputReserveRatio", "輸出保留比例", "number", 0, 1, "輸出預算再受最小與最大輸出 token 限制。"),
        new NyaChatSettingSpec("MinOutputTokens", "最小輸出 token", "integer", 1, 1000000, "輸出保留額的下限。"),
        new NyaChatSettingSpec("MaxOutputTokens", "最大輸出 token", "integer", 1, 1000000, "輸出保留額的上限。"),
        new NyaChatSettingSpec("ContextSafetyMargin", "上下文安全餘額", "integer", 0, 1000000, "額外扣除的 token，吸收估算誤差。"),
        new NyaChatSettingSpec("MaxReplyLength", "單段回覆字數", "integer", 1, 100000, "長回覆的分段字元上限。"),
        new NyaChatSettingSpec("MaxToolRounds", "工具最大回合", "integer", 1, 20, "工具閉環可續查的回合上限。"),
        new NyaChatSettingSpec("PerToolResultMaxTokens", "單一工具結果 token", "integer", 1, 1000000, "過長結果截尾後再送模型。"),
        new NyaChatSettingSpec("ToolExecTimeoutSeconds", "工具逾時秒數", "integer", 1, 3600, "限制單一工具執行時間。"),
        new NyaChatSettingSpec("StripMarkdown", "轉為純文字", "bool", null, null, "將 Markdown 降級為 LINE 純文字。"),
        new NyaChatSettingSpec("EnableMultiPartReply", "允許分段回覆", "bool", null, null, "長回覆分為多段傳送。"),
        new NyaChatSettingSpec("DefaultSoulId", "預設 Soul", "string", null, null, "未綁定人格的使用者使用此 Soul。"),
        new NyaChatSettingSpec("AuditEnabled", "對話稽核", "bool", null, null, "設定變更本身一律留下稽核紀錄。")
    };

    public static JsonObject Merge(NyaChatConfig current, JsonElement patch, out string[] changed)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("設定必須是 JSON object。");
        var node = JsonSerializer.SerializeToNode(current)!.AsObject();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in patch.EnumerateObject())
        {
            var spec = All.FirstOrDefault(s => s.Key.Equals(property.Name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"{property.Name} 不可熱更新；啟動設定請修改 appsettings，模型與路由請使用 LLM 模型頁。");
            if (!keys.Add(spec.Key)) throw new ArgumentException($"重複設定：{spec.Key}");
            ValidateValue(spec, property.Value);
            node[spec.Key] = JsonNode.Parse(property.Value.GetRawText());
        }
        if (keys.Count == 0) throw new ArgumentException("未提供可編輯設定。");
        var config = node.Deserialize<NyaChatConfig>()!;
        if (config.MinOutputTokens > config.MaxOutputTokens) throw new ArgumentException("MinOutputTokens 不可大於 MaxOutputTokens。");
        if (config.MinOutputTokens + (long)config.ContextSafetyMargin >= config.DefaultMaxContextTokens)
            throw new ArgumentException("最小輸出 token 加安全餘額必須小於預設上下文 token。");
        if (config.SummarizeKeepRecentCount >= config.SummarizeTriggerCount)
            throw new ArgumentException("摘要保留近期則數必須小於摘要觸發則數。");
        changed = keys.ToArray();
        return node;
    }

    private static void ValidateValue(NyaChatSettingSpec spec, JsonElement value)
    {
        if (spec.Type == "bool" && value.ValueKind is JsonValueKind.True or JsonValueKind.False) return;
        if (spec.Type == "string" && value.ValueKind == JsonValueKind.String &&
            System.Text.RegularExpressions.Regex.IsMatch(value.GetString()!, @"^[A-Za-z0-9_\-]{1,64}$")) return;
        if (spec.Type is "integer" or "number" && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number) && double.IsFinite(number) &&
            (spec.Type != "integer" || value.TryGetInt32(out _)) && number >= spec.Min && number <= spec.Max) return;
        throw new ArgumentException($"{spec.Key} 必須是 {spec.Type}" +
            (spec.Min.HasValue ? $"，範圍 [{spec.Min}, {spec.Max}]。" : "，且不可為空。"));
    }

    public static NyaChatConfig MaskSecrets(NyaChatConfig config)
    {
        var copy = JsonSerializer.Deserialize<NyaChatConfig>(JsonSerializer.Serialize(config))!;
        copy.ChatApiKey = NyaSecretMask.Mask(copy.ChatApiKey);
        foreach (var profile in copy.LlmProfiles.Values) profile.ApiKey = NyaSecretMask.Mask(profile.ApiKey);
        return copy;
    }
}
