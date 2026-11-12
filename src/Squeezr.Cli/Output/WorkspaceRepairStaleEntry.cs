using Squeezr.Cli.Commands;

namespace Squeezr.Cli.Output;

public sealed record WorkspaceRepairStaleEntry(
    string Name,
    string StoreKey,
    string RootPath,
    long? StoreBytes,
    string StoreState,
    string Category = WorkspaceRepairPruner.CategoryStale,
    // The parent link a row carried when it was still a lane; null for a row that never was one.
    string? ParentRootPath = null);
