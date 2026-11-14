using System.Text.Json.Serialization;

namespace Squeezr.Cli.Commands;

public sealed record HookSessionStartAdvice(
    [property: JsonPropertyName("hookSpecificOutput")] HookSessionStartAdviceOutput HookSpecificOutput);
