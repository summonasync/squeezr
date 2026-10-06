namespace Squeezr.Core.Workspace;

public sealed class WorkspaceStoreRemovalException : IOException
{
    public WorkspaceStoreRemovalException(string storeKey, Exception innerException)
        : base($"Could not remove the store dir for store key '{storeKey}'.", innerException)
    {
        StoreKey = storeKey;
    }

    public string StoreKey { get; }
}
