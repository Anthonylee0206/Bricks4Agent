using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 工具參數驗證（Bug #4 嚴審修復）。<see cref="NyaToolSchema"/> 宣告了 required / enum，但執行邊界
/// 從未強制——原始 <c>ArgumentsJson</c> 直接丟給插件，<c>Required</c>/<c>Enum</c> 形同裝飾、且每個插件
/// 各自手刨防禦。本驗證器在 Orchestrator 分派前依 schema 校驗 LLM 提供的參數，失敗時把原因回饋給 LLM
/// 使其能修正，而非讓插件靜默吃壞參數。
/// </summary>
public static class NyaToolArgValidator
{
    /// <summary>依 schema 驗證參數 JSON。回傳 (是否通過, 失敗原因)。</summary>
    public static (bool Ok, string? Error) Validate(NyaToolSchema schema, string argumentsJson)
    {
        var hasRequired = schema.Parameters.Any(p => p.Required);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
        }
        catch (JsonException)
        {
            // 無必填參數時容許壞 / 空 JSON（視為無參數）；有必填則視為缺參數。
            return hasRequired
                ? (false, $"工具 '{schema.Name}' 的參數不是合法 JSON。")
                : (true, null);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return hasRequired
                    ? (false, $"工具 '{schema.Name}' 需要物件型別的參數。")
                    : (true, null);
            }

            foreach (var p in schema.Parameters)
            {
                var present = root.TryGetProperty(p.Name, out var el) && el.ValueKind != JsonValueKind.Null;

                if (p.Required && (!present || IsBlankString(el)))
                    return (false, $"缺少必填參數 '{p.Name}'（{p.Description}）。");

                if (present && p.Enum is { Count: > 0 } && el.ValueKind == JsonValueKind.String)
                {
                    var v = el.GetString();
                    if (v != null && !p.Enum.Contains(v))
                        return (false,
                            $"參數 '{p.Name}' 的值 '{v}' 不在允許範圍：{string.Join(", ", p.Enum)}。");
                }
            }

            return (true, null);
        }
    }

    private static bool IsBlankString(JsonElement el)
        => el.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(el.GetString());
}
