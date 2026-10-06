namespace Squeezr.Core.Storage;

public sealed record WorkspaceSiblingEntry(
    string Name,
    string RootPath,
    string StoreKey,
    bool Current,
    bool Stale);

public sealed record WorkspaceSiblingSnapshot(
    IReadOnlyList<WorkspaceSiblingEntry> Workspaces,
    int Omitted)
{
    public const int Cap = 12;

    public static WorkspaceSiblingSnapshot Empty { get; } = new([], 0);

    public static WorkspaceSiblingSnapshot Build(string? currentRootPath)
    {
        var entries = WorkspaceRegistry.Load();
        if (entries.Count < 2)
        {
            return Empty;
        }

        var currentStoreKey = currentRootPath is null
            ? null
            : WorkspaceRegistry.FindByRootPath(currentRootPath)?.StoreKey;

        var listed = new List<WorkspaceSiblingEntry>(Cap);
        // the existence probe runs on the listed window only, so a registry of hundreds still costs Cap stat calls
        foreach (var entry in OrderByCurrent(entries, currentStoreKey).Take(Cap))
        {
            listed.Add(new WorkspaceSiblingEntry(
                entry.Name,
                entry.RootPath,
                entry.StoreKey,
                Current: IsCurrent(entry, currentStoreKey),
                Stale: !Directory.Exists(entry.RootPath)));
        }

        return new WorkspaceSiblingSnapshot(listed, entries.Count - listed.Count);
    }

    private static IOrderedEnumerable<WorkspaceRegistryEntry> OrderByCurrent(
        IReadOnlyList<WorkspaceRegistryEntry> entries,
        string? currentStoreKey) =>
        entries.OrderBy(entry => IsCurrent(entry, currentStoreKey) ? 0 : 1);

    private static bool IsCurrent(WorkspaceRegistryEntry entry, string? currentStoreKey) =>
        currentStoreKey is not null
        && string.Equals(entry.StoreKey, currentStoreKey, StringComparison.Ordinal);
}
