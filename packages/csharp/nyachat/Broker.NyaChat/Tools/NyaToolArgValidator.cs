using System.Text.Json;
using System.Text.RegularExpressions;
using Broker.NyaChat.Abstractions;

namespace Broker.NyaChat;

public static class NyaToolArgValidator
{
    public static (bool Ok, string? Error) Validate(NyaToolSchema schema, string argumentsJson)
    {
        var error = ValidateError(schema, argumentsJson);
        return (error == null, error?.Message);
    }

    public sealed record ArgumentError(string Field, string Message);

    public static ArgumentError? ValidateError(NyaToolSchema schema, string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new("arguments", "arguments 預期 object。");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                if (!names.Add(property.Name)) return new(property.Name, $"{property.Name} 不可重複。");
                if (!schema.Parameters.Any(p => p.Name == property.Name)) return new(property.Name, $"{property.Name} 不允許額外屬性。");
            }
            foreach (var parameter in schema.Parameters)
            {
                if (!root.TryGetProperty(parameter.Name, out var value))
                {
                    if (parameter.Required) return new(parameter.Name, $"{parameter.Name} 必填，預期 {parameter.Type}。");
                    continue;
                }
                var error = Check(parameter, value, parameter.Name);
                if (error != null) return error;
            }
            if (schema.AnyOfRequired.Count > 0 && !schema.AnyOfRequired.Any(fields => fields.All(field => root.TryGetProperty(field, out _))))
                return new(string.Join("|", schema.AnyOfRequired.SelectMany(fields => fields)), "必須提供 schema anyOf 中至少一組欄位。");
            foreach (var fields in schema.ExcludedTogether)
                if (fields.All(field => root.TryGetProperty(field, out _))) return new(string.Join("|", fields), "schema 禁止同時提供這些欄位。");
            foreach (var condition in schema.RequiredWhen)
                if (root.TryGetProperty(condition.Field, out var trigger) && trigger.ValueKind == JsonValueKind.String && trigger.GetString() == condition.Value && !root.TryGetProperty(condition.RequiredField, out _))
                    return new(condition.RequiredField, $"{condition.Field}={condition.Value} 時必須提供 {condition.RequiredField}。");
            return null;
        }
        catch (JsonException)
        {
            return new("arguments", "arguments 預期合法 JSON object。");
        }
    }

    private static ArgumentError? Check(NyaToolParam parameter, JsonElement value, string field)
    {
        var validType = parameter.Type switch
        {
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) && decimal.Truncate(number) == number,
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => value.ValueKind == JsonValueKind.Array,
            "object" => value.ValueKind == JsonValueKind.Object,
            _ => false
        };
        if (!validType) return new(field, $"{field} 預期 {parameter.Type}，收到 {value.ValueKind}。");
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            var length = text.EnumerateRunes().Count();
            if (parameter.Required && string.IsNullOrWhiteSpace(text)) return new(field, $"{field} 預期非空 string。");
            if (parameter.Enum is { Count: > 0 } && !parameter.Enum.Contains(text)) return new(field, $"{field} 預期 enum [{string.Join(", ", parameter.Enum)}]。");
            if (parameter.MinLength.HasValue && length < parameter.MinLength || parameter.MaxLength.HasValue && length > parameter.MaxLength)
                return new(field, $"{field} 預期長度 [{parameter.MinLength ?? 0}, {parameter.MaxLength}]。");
            if (parameter.Pattern != null && !Regex.IsMatch(text, parameter.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                return new(field, $"{field} 預期 pattern {parameter.Pattern}。");
            if (parameter.Format == "date" && !DateTime.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _))
                return new(field, $"{field} 預期有效 yyyy-MM-dd 日期。");
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetDouble();
            if (parameter.Minimum.HasValue && number < parameter.Minimum || parameter.Maximum.HasValue && number > parameter.Maximum)
                return new(field, $"{field} 預期範圍 [{parameter.Minimum}, {parameter.Maximum}]。");
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var count = value.GetArrayLength();
            if (parameter.MinItems.HasValue && count < parameter.MinItems || parameter.MaxItems.HasValue && count > parameter.MaxItems)
                return new(field, $"{field} 預期項目數 [{parameter.MinItems ?? 0}, {parameter.MaxItems}]。");
            if (parameter.Items != null)
            {
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    var error = Check(parameter.Items, item, $"{field}[{index++}]");
                    if (error != null) return error;
                }
            }
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !parameter.Properties.Any(p => p.Name == property.Name))
                    return new(field + "." + property.Name, $"{field}.{property.Name} 不允許額外或重複屬性。");
            foreach (var property in parameter.Properties)
            {
                if (!value.TryGetProperty(property.Name, out var child))
                {
                    if (property.Required) return new(field + "." + property.Name, $"{field}.{property.Name} 必填，預期 {property.Type}。");
                    continue;
                }
                var error = Check(property, child, field + "." + property.Name);
                if (error != null) return error;
            }
            foreach (var condition in parameter.RequiredWhen)
                if (value.TryGetProperty(condition.Field, out var trigger) && trigger.ValueKind == JsonValueKind.String && trigger.GetString() == condition.Value && !value.TryGetProperty(condition.RequiredField, out _))
                    return new(field + "." + condition.RequiredField, $"{field}.{condition.Field}={condition.Value} 時必須提供 {condition.RequiredField}。");
        }
        return null;
    }
}
