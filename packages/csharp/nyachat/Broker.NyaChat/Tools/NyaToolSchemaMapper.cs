using System.Text.Json;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

public static class NyaToolSchemaMapper
{
    public static NyaLlmParameterProperty Property(NyaToolParam parameter) => new()
    {
        Type = parameter.Type, Description = parameter.Description,
        Enum = parameter.Enum is { Count: > 0 } ? JsonSerializer.Serialize(parameter.Enum) : null,
        MinLength = parameter.MinLength ?? (parameter.Required && parameter.Type == "string" ? 1 : null), MaxLength = parameter.MaxLength,
        Minimum = parameter.Minimum, Maximum = parameter.Maximum,
        MinItems = parameter.MinItems, MaxItems = parameter.MaxItems,
        Pattern = parameter.Pattern ?? (parameter.Required && parameter.Type == "string" ? @"\S" : null), Format = parameter.Format,
        Items = parameter.Items == null ? null : Property(parameter.Items),
        Properties = parameter.Properties.ToDictionary(p => p.Name, Property), Required = parameter.Properties.Where(p => p.Required).Select(p => p.Name).ToList(),
        RulesJson = Rules(new NyaToolSchema { RequiredWhen = parameter.RequiredWhen })
    };

    public static NyaLlmTool ToLlmTool(NyaToolSchema schema) => new()
    {
        Function = new NyaLlmFunction
        {
            Name = schema.Name, Description = schema.Description,
            Parameters = new NyaLlmFunctionParameters
            {
                AdditionalProperties = false,
                RulesJson = Rules(schema),
                Properties = schema.Parameters.ToDictionary(p => p.Name, Property),
                Required = schema.Parameters.Where(p => p.Required).Select(p => p.Name).ToList()
            }
        }
    };

    private static string Rules(NyaToolSchema schema)
    {
        var rules = new System.Text.Json.Nodes.JsonObject();
        if (schema.AnyOfRequired.Count > 0)
            rules["anyOf"] = new System.Text.Json.Nodes.JsonArray(schema.AnyOfRequired.Select(fields => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["required"] = JsonSerializer.SerializeToNode(fields) }).ToArray());
        var all = new System.Text.Json.Nodes.JsonArray();
        foreach (var fields in schema.ExcludedTogether)
            all.Add(new System.Text.Json.Nodes.JsonObject { ["not"] = new System.Text.Json.Nodes.JsonObject { ["required"] = JsonSerializer.SerializeToNode(fields) } });
        foreach (var condition in schema.RequiredWhen)
            all.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["if"] = new System.Text.Json.Nodes.JsonObject { ["properties"] = new System.Text.Json.Nodes.JsonObject { [condition.Field] = new System.Text.Json.Nodes.JsonObject { ["const"] = condition.Value } }, ["required"] = new System.Text.Json.Nodes.JsonArray(condition.Field) },
                ["then"] = new System.Text.Json.Nodes.JsonObject { ["required"] = new System.Text.Json.Nodes.JsonArray(condition.RequiredField) }
            });
        if (all.Count > 0) rules["allOf"] = all;
        return rules.ToJsonString();
    }
}
