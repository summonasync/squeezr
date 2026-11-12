using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Configuration;
using Squeezr.Core.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

internal sealed record WorkspaceDropResult(
    IReadOnlyList<string> RemovedPaths,
    string? Collection,
    string CollectionState);

// The one door that ends a workspace store: registry row and store dir first, through
// WorkspaceStore.DropByStoreKey, then the collection this root names. Every verb that kills a store
// (`drop --store-key`, `worktree drop`, the repair sweep) comes through here, so the order is stated once
// and cannot drift between them: a collection is touched only after the row owning it is gone, which is the
// direction that leaves a live store's vectors alone when a removal fails halfway.
internal static class WorkspaceStoreDropper
{
    public const string CollectionRemoved = "removed";
    public const string CollectionUnavailable = "unavailable";
    public const string CollectionFailed = "failed";
    public const string CollectionNotConfigured = "not-configured";

    public static async Task<WorkspaceDropResult> DropByStoreKeyAsync(
        string storeKey,
        CancellationToken cancellationToken)
    {
        _ = WorkspaceStorePaths.RequireStoreDir(storeKey);
        var entry = WorkspaceRootResolver.GuardRegistryIo(() => WorkspaceRegistry.FindByStoreKey(storeKey))
            ?? throw new WorkspaceException($"No workspace is registered with store key '{storeKey}'.");

        return await DropAsync(storeKey, entry.RootPath, TryOpenVectorStore(entry.RootPath), cancellationToken);
    }

    public static async Task<WorkspaceDropResult> DropAsync(
        string storeKey,
        string registeredRoot,
        IVectorStore? vectorStore,
        CancellationToken cancellationToken)
    {
        var removed = WorkspaceRootResolver.GuardRegistryIo(
            () => WorkspaceStore.DropByStoreKey(storeKey, StaleLockWarningSink.ToStderr));

        if (vectorStore is null)
        {
            return new WorkspaceDropResult(removed, null, CollectionNotConfigured);
        }

        var collection = QdrantVectorStore.GetCollectionName(registeredRoot);
        return new WorkspaceDropResult(removed, collection, await RemoveCollectionAsync(vectorStore, cancellationToken));
    }

    // A store handle comes off the config the root still carries. Having none is a fact about this workspace,
    // not a failure: the removal goes through regardless and reports that the collection was left alone.
    public static IVectorStore? TryOpenVectorStore(string root)
    {
        try
        {
            var config = ConfigFile.Load(root);
            return new QdrantVectorStore(config.Qdrant, root);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"warning: Qdrant config unavailable for '{root}': {ex.Message}");
            return null;
        }
    }

    public static async Task<string> RemoveCollectionAsync(
        IVectorStore vectorStore,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!await vectorStore.IsAvailableAsync(cancellationToken))
            {
                Console.Error.WriteLine(
                    "warning: Qdrant is not available; the vector collection may still exist in the shared volume.");
                return CollectionUnavailable;
            }

            await vectorStore.DeleteCollectionAsync(cancellationToken);
            return CollectionRemoved;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: failed to delete the Qdrant collection: {ex.Message}");
            return CollectionFailed;
        }
    }
}
