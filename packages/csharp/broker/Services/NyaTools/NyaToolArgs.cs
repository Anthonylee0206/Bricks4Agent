using System.Text.Json;

namespace Broker.Services.NyaTools;

/// <summary>broker adapter 插件共用的 tool 參數解析小工具。</summary>
internal static class NyaToolArgs
{
    /// <summary>從 tool_call 的參數 JSON 取出指定字串欄位；缺失 / 解析失敗回 null。</summary>
    public static string? GetString(string argumentsJson, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString();
        }
        catch
        {
            // ignore parse errors — caller treats null as missing
        }
        return null;
    }
}
