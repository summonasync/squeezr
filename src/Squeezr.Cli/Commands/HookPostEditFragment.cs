using System.Text.Json.Serialization;

namespace Squeezr.Cli.Commands;

// A MultiEdit-style item: the host's real fragments carry old_string/new_string, only the file_path
// member matters here and everything else deserializes past it.
public sealed record HookPostEditFragment
{
    [JsonPropertyName("file_path")]
    public string? FilePath { get; init; }
}
