namespace Squeezr.Cli.Output;

public sealed record WorkspaceListingResult(
    IReadOnlyList<WorkspaceListEntry> Workspaces,
    IReadOnlyList<WorkspaceOrphanEntry> Orphans);
