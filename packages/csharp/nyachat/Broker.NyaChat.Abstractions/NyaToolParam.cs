namespace Broker.NyaChat.Abstractions;

public sealed class NyaToolParam
{
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public int? MinItems { get; init; }
    public int? MaxItems { get; init; }
    public string? Pattern { get; init; }
    public string? Format { get; init; }
    public NyaToolParam? Items { get; init; }
    public IReadOnlyList<NyaToolParam> Properties { get; init; } = Array.Empty<NyaToolParam>();
    public IReadOnlyList<NyaToolCondition> RequiredWhen { get; init; } = Array.Empty<NyaToolCondition>();

    public string Name { get; init; } = "";

    public string Type { get; init; } = "string";

    public string Description { get; init; } = "";

    public bool Required { get; init; }

    public IReadOnlyList<string>? Enum { get; init; }
}
