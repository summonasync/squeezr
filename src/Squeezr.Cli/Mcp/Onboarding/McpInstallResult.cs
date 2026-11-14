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
    bool PostEditHooksArmed = false,
    // Additive per 035: the SessionStart block this pass wrote — path plus the exact bytes — present only when
    // --session-start was asked and a verified layout took it. Its absence is the claim that nothing was armed.
    SessionStartHookWrite? SessionStart = null);
