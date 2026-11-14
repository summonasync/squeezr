using System.Text.Json.Serialization;

namespace Squeezr.Cli.Commands;

public sealed record HookSessionStartAdviceOutput(
    [property: JsonPropertyName("hookEventName")] string HookEventName,
    [property: JsonPropertyName("additionalContext")] string AdditionalContext);
