using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

public static class WorkspaceListing
{
    public static IReadOnlyList<WorkspaceListEntry> Load() =>
        WorkspaceRegistry.Load()
            .Select(entry => new WorkspaceListEntry(
                entry.Name,
                entry.RootPath,
                entry.StoreKey,
                entry.CreatedAt,
                entry.LastUsedAt,
                Stale: !Directory.Exists(entry.RootPath)))
            .ToList();
}
