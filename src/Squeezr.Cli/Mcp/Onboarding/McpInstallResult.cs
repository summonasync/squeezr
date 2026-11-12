namespace Squeezr.Cli.Mcp.Onboarding;

public sealed record McpInstallResult(
    string Client,
    string Scope,
    string Method,
    string ConfigPath,
    bool InstructionsWritten,
    string? Warning,
    string? Error,
    bool HooksArmed = false);
