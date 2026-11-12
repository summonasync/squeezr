namespace Squeezr.Cli.Mcp.Onboarding;

public sealed record McpRemoveResult(
    string Client,
    string Scope,
    string Method,
    string ConfigPath,
    bool InstructionsRemoved,
    string? Warning,
    string? Error,
    bool HooksRemoved = false);
