namespace Squeezr.Cli.Mcp.Onboarding;

public sealed record McpInstallResult(
    string Client,
    string Scope,
    string Method,
    string ConfigPath,
    bool InstructionsWritten,
    string? Warning,
    string? Error,
    bool HooksArmed = false,
    // Additive per 025/R5: the PostToolUse arm reported apart from the pre-read arm, 035 envelope discipline.
    bool PostEditHooksArmed = false);
