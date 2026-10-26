using System.Text.Json.Serialization;

namespace Squeezr.Cli.Commands;

public sealed record HookPreReadInput
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_input")]
    public HookPreReadToolInput? ToolInput { get; init; }
}
