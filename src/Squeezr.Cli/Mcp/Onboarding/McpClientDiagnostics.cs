namespace Squeezr.Cli.Mcp.Onboarding;

public sealed record McpClientDiagnostics(
    string Client,
    bool Installed,
    bool UserScopeEntry,
    bool ProjectScopeEntry,
    string UserScopeMethod,
    string ProjectScopeMethod,
    bool SqueezrInPath,
    bool WorkspaceIndexed,
    string? HeadlessStatus,
    IReadOnlyList<string> Warnings,
    string? UserCommand = null,
    string? ProjectCommand = null,
    bool UserStale = false,
    bool ProjectStale = false,
    string? ExpectedCommand = null,
    ToolErrorPayload? IndexState = null,
    // Additive per 035: what the store's watch claim says about this workspace — the checkable half of the
    // --watch default flip. Existing keys keep their values; a store nothing resolves carries no watch object.
    StoreWatchDiagnostics? Watch = null);
