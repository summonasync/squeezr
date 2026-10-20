namespace Squeezr.Cli.Output;

public sealed record WorkspaceRepairCategoryCounts(
    int Adopted,
    int NeedsRoot,
    int StalePruned,
    int HusksPruned);
