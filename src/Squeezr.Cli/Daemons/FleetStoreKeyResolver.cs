using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Daemons;

public static class FleetStoreKeyResolver
{
    public static WorkspaceRegistryEntry Require(string storeKey) =>
        WorkspaceRootResolver.GuardRegistryIo(() => WorkspaceRegistry.FindByStoreKey(storeKey))
        ?? throw new WorkspaceException($"No workspace is registered with store key '{storeKey}'.");

    // The one answer to "can this machine reach the store this row names". A key that is not a single path
    // segment or a root that is gone both mean a registry row left behind by another host or a deleted
    // workspace: readers may still describe it, writers must not record a wish for it (113/R6).
    public static bool Resolves(string storeKey, string rootPath) =>
        WorkspaceStorePaths.IsSingleSegmentKey(storeKey) && Directory.Exists(rootPath);

    public static WorkspaceRegistryEntry RequireResolvable(WorkspaceRegistryEntry entry, string action)
    {
        if (Resolves(entry.StoreKey, entry.RootPath))
        {
            return entry;
        }

        throw new WorkspaceException(
            $"Cannot {action} workspace '{entry.Name}': root path '{entry.RootPath}' does not exist; " +
            $"remove the stale entry with 'squeezr drop --store-key {entry.StoreKey}'.");
    }
}
