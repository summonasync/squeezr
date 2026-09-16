using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Daemons;

public static class FleetStoreKeyResolver
{
    public static WorkspaceRegistryEntry Require(string storeKey) =>
        WorkspaceRootResolver.GuardRegistryIo(() => WorkspaceRegistry.FindByStoreKey(storeKey))
        ?? throw new WorkspaceException($"No workspace is registered with store key '{storeKey}'.");
}
