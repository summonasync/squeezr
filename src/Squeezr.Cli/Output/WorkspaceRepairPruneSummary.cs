namespace Squeezr.Cli.Output;

public sealed record WorkspaceRepairPruneSummary(
    string Mode,
    IReadOnlyList<WorkspaceRepairPrunedEntry> Rows,
    long TotalStoreBytes,
    int StoresRemoved);
