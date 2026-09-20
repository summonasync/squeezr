using System.Text.Json.Serialization;

namespace Squeezr.Cli.Commands;

public sealed record HookPreReadToolInput
{
    [JsonPropertyName("file_path")]
    public string? FilePath { get; init; }

    [JsonPropertyName("offset")]
    public int? Offset { get; init; }

    [JsonPropertyName("limit")]
    public int? Limit { get; init; }
}
