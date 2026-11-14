namespace Squeezr.Cli.Mcp.Onboarding;

public sealed record McpRemoveResult(
    string Client,
    string Scope,
    string Method,
    string ConfigPath,
    bool InstructionsRemoved,
    string? Warning,
    string? Error,
    bool HooksRemoved = false,
    // Additive per 025/R5: the PostToolUse arm reported apart from the pre-read arm, 035 envelope discipline.
    bool PostEditHooksRemoved = false,
    // Additive per 035: a block install can arm is one remove has to take back, or the hook outlives the
    // registration that armed it. True only when a managed SessionStart block was really found and stripped.
    bool SessionStartRemoved = false);
