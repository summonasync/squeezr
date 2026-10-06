namespace Squeezr.Cli.Output;

public sealed record WorkspaceRepairPrunedEntry(
    string StoreKey,
    string Name,
    string RootPath,
    string Category,
    string Action,
    long? StoreBytes,
    IReadOnlyList<string> RemovedPaths,
    string? Failure = null);
