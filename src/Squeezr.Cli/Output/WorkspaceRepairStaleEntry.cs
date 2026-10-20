using Squeezr.Cli.Commands;

namespace Squeezr.Cli.Output;

public sealed record WorkspaceRepairStaleEntry(
    string Name,
    string StoreKey,
    string RootPath,
    long? StoreBytes,
    string StoreState,
    string Category = WorkspaceRepairPruner.CategoryStale);
