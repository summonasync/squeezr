using System.Text.Json.Serialization;

namespace Squeezr.Cli.Commands;

public sealed record HookPostEditInput
{
    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public HookPostEditToolInput? ToolInput { get; init; }
}
