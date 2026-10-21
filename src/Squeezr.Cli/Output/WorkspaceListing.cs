using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

public static class WorkspaceListing
{
    public static WorkspaceListingResult Load()
    {
        var scan = OrphanScanner.Scan();
        var huskKeys = scan.Husks.Select(husk => husk.StoreKey).ToHashSet(StringComparer.Ordinal);
        var staleKeys = scan.Stale.Select(stale => stale.StoreKey).ToHashSet(StringComparer.Ordinal);
        var entries = WorkspaceRegistry.Load();
        var collidingNames = CollidingNames.SharedByLiveEntries(entries);
        var workspaces = entries
            .Select(entry => new WorkspaceListEntry(
                entry.Name,
                entry.RootPath,
                entry.StoreKey,
                entry.CreatedAt,
                entry.LastUsedAt,
                Stale: staleKeys.Contains(entry.StoreKey),
                Husk: huskKeys.Contains(entry.StoreKey) ? true : null,
                NameCollision: collidingNames.Contains(entry.Name) ? true : null))
            .ToList();
        var orphans = scan.Orphans
            .Select(orphan => new WorkspaceOrphanEntry(
                orphan.Name,
                orphan.StoreKey,
                orphan.IsAdoptable ? "adoptable" : orphan.RootPath is null ? "legacy" : "root-gone",
                orphan.RootPath))
            .ToList();

        return new WorkspaceListingResult(workspaces, orphans);
    }
}
