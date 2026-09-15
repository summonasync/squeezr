namespace Squeezr.Core.Storage;

public sealed record WorkspaceResolution(
    string RootPath,
    string? StoreKey,
    string? StoreDir,
    bool IsWorkspace,
    bool MigratedFromLegacy,
    bool ViaClaudeProjectDir = false);
