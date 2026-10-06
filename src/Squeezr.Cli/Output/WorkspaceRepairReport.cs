namespace Squeezr.Cli.Output;

public sealed record WorkspaceRepairReport(
    IReadOnlyList<WorkspaceRepairAdoptedEntry> Adopted,
    IReadOnlyList<WorkspaceRepairNeedsRootEntry> NeedsRoot,
    IReadOnlyList<WorkspaceRepairHuskEntry> Husks,
    IReadOnlyList<WorkspaceRepairStaleEntry> Stale,
    WorkspaceRepairPruneSummary Prune);
