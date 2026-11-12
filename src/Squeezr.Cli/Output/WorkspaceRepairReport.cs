namespace Squeezr.Cli.Output;

// FailedAdoptions is present only when the adopt phase actually lost rows, mirroring the removedPaths
// omission rule: a run where every adoption went through keeps the document it always had.
public sealed record WorkspaceRepairReport(
    IReadOnlyList<WorkspaceRepairAdoptedEntry> Adopted,
    IReadOnlyList<WorkspaceRepairNeedsRootEntry> NeedsRoot,
    IReadOnlyList<WorkspaceRepairHuskEntry> Husks,
    IReadOnlyList<WorkspaceRepairStaleEntry> Stale,
    WorkspaceRepairPruneSummary Prune,
    WorkspaceRepairCategoryCounts Counts,
    IReadOnlyList<WorkspaceRepairFailedAdoptionEntry>? FailedAdoptions = null,
    // Absent means the collection arm never ran; a reader must not take it as "nothing was unreferenced".
    WorkspaceRepairCollectionsReport? Collections = null);
