namespace Broker.NyaChat.Abstractions;

public sealed class NyaToolSchema
{
    public IReadOnlyList<string[]> AnyOfRequired { get; init; } = Array.Empty<string[]>();
    public IReadOnlyList<string[]> ExcludedTogether { get; init; } = Array.Empty<string[]>();
    public IReadOnlyList<NyaToolCondition> RequiredWhen { get; init; } = Array.Empty<NyaToolCondition>();

    public string Name { get; init; } = "";

    public string Description { get; init; } = "";

    public IReadOnlyList<NyaToolParam> Parameters { get; init; } = Array.Empty<NyaToolParam>();

    public string? Group { get; init; }

    public bool DefaultEnabled { get; init; }

    public string? RequiredPermission { get; init; }

    public string CapabilityStatement { get; init; } = "";
}

public sealed record NyaToolCondition(string Field, string Value, string RequiredField);
