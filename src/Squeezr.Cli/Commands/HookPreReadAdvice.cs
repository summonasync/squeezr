using System.Text.Json.Serialization;

namespace Squeezr.Cli.Commands;

public sealed record HookPreReadAdvice(
    [property: JsonPropertyName("hookSpecificOutput")] HookPreReadAdviceOutput HookSpecificOutput);
