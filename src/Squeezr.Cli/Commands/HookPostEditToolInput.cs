using System.Text.Json.Serialization;

namespace Squeezr.Cli.Commands;

public sealed record HookPostEditToolInput
{
    [JsonPropertyName("file_path")]
    public string? FilePath { get; init; }

    [JsonPropertyName("notebook_path")]
    public string? NotebookPath { get; init; }

    [JsonPropertyName("edits")]
    public IReadOnlyList<HookPostEditFragment>? Edits { get; init; }
}
