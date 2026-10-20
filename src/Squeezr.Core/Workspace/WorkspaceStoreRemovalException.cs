namespace Squeezr.Core.Workspace;

public sealed class WorkspaceStoreRemovalException : IOException
{
    private WorkspaceStoreRemovalException(string? storeKey, string message, Exception innerException)
        : base(message, innerException)
    {
        StoreKey = storeKey;
    }

    public WorkspaceStoreRemovalException(string storeKey, Exception innerException)
        : this(storeKey, $"Could not remove the store dir for store key '{storeKey}'.", innerException)
    {
    }

    // The cwd route holds only the resolved store dir path, never a key: naming a key there would
    // invent a fact that route cannot know.
    public static WorkspaceStoreRemovalException ForStoreDir(string storeDir, Exception innerException) =>
        new(null, $"Could not remove the workspace store dir '{storeDir}'.", innerException);

    public string? StoreKey { get; }
}
