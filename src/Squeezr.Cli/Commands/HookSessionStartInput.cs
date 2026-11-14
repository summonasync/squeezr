using System.Text.Json.Serialization;

namespace Squeezr.Cli.Commands;

public sealed record HookSessionStartInput
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("cwd")]
    public string? Cwd { get; init; }

    [JsonPropertyName("hook_event_name")]
    public string? HookEventName { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }
}
