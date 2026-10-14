namespace Broker.NyaChat.Abstractions;

public sealed class NyaToolResult
{
    public string Outcome { get; init; } = "completed";
    public IReadOnlyList<object> Effects { get; init; } = Array.Empty<object>();

    public static NyaToolResult Envelope(object? data = null, string outcome = "completed", IReadOnlyList<object>? effects = null, IReadOnlyList<string>? directReplies = null, bool requiresExactOutput = false)
    {
        var items = effects ?? Array.Empty<object>();
        return new NyaToolResult
        {
            Success = true, Outcome = outcome, Effects = items,
            DirectReplies = directReplies,
            RequiresExactOutput = requiresExactOutput,
            LlmContent = System.Text.Json.JsonSerializer.Serialize(new { ok = true, outcome, error = (object?)null, data, effects = items }, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        };
    }

    public static NyaToolResult Error(string code, string field, string message, object? data = null)
        => new()
        {
            Success = false, Outcome = "error",
            LlmContent = System.Text.Json.JsonSerializer.Serialize(new { ok = false, outcome = "error", error = new { code, field, message }, data, effects = Array.Empty<object>() }, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
        };

    public bool Success { get; init; }

    public string LlmContent { get; init; } = "";

    public IReadOnlyList<string>? DirectReplies { get; init; }

    public bool RequiresExactOutput { get; init; }

    public static NyaToolResult Ok(string llmContent, IReadOnlyList<string>? directReplies = null, bool requiresExactOutput = false)
        => Envelope(new { result = llmContent }, directReplies: directReplies, requiresExactOutput: requiresExactOutput);

    public static NyaToolResult Fail(string llmContent)
        => Error("tool_failed", "tool", llmContent);
}
