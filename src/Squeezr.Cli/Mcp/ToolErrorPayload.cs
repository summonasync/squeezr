using System.Text.Json.Serialization;

namespace Squeezr.Cli.Mcp;

public sealed record ToolErrorPayload(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("rootPath"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RootPath = null,
    [property: JsonPropertyName("nextSteps"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? NextSteps = null);
