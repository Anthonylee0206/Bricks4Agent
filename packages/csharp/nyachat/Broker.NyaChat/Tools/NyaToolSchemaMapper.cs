using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

/// <summary>
/// 把契約 <see cref="NyaToolSchema"/>（含多個 <see cref="NyaToolParam"/>）對映為內部 LLM 工具定義
/// <see cref="NyaLlmTool"/>。
/// </summary>
/// <remarks>
/// 純函式、無狀態。此映射讓「外部工具被強制只有單一 <c>query</c> 參數」的舊限制消失：
/// 插件可宣告 symbol / amount / action 等任意多參數、型別與 enum。
/// </remarks>
public static class NyaToolSchemaMapper
{
    public static NyaLlmTool ToLlmTool(NyaToolSchema schema)
    {
        var properties = new Dictionary<string, NyaLlmParameterProperty>();
        var required   = new List<string>();

        foreach (var p in schema.Parameters)
        {
            properties[p.Name] = new NyaLlmParameterProperty
            {
                Type        = string.IsNullOrWhiteSpace(p.Type) ? "string" : p.Type,
                Description = p.Description,
                // NyaLlmClient.BuildToolsJson 以 JsonNode.Parse(prop.Enum) 解析 → 需為 JSON 陣列字串
                Enum = p.Enum is { Count: > 0 }
                    ? JsonSerializer.Serialize(p.Enum)
                    : null
            };
            if (p.Required)
                required.Add(p.Name);
        }

        return new NyaLlmTool
        {
            Function = new NyaLlmFunction
            {
                Name        = schema.Name,
                Description = schema.Description,
                Parameters  = new NyaLlmFunctionParameters
                {
                    Properties = properties,
                    Required   = required
                }
            }
        };
    }
}
