namespace Squeezr.Core.Workspace;

// The store dir is gone and the registry row is not: the removal is a fact, the registry state is not,
// so the verdict is "removed: unverified" and the paths have to outlive the failed write instead of
// being lost with it. Derives from IOException so the prune sweep keeps catching it; anything that only
// sees IOException must not repaint it as a busy registry.
public sealed class WorkspaceStoreRemovalUnverifiedException : IOException
{
    public WorkspaceStoreRemovalUnverifiedException(
        IReadOnlyList<string> removedStoreDirs,
        Exception innerException)
        : base(BuildMessage(removedStoreDirs, innerException), innerException)
    {
        RemovedStoreDirs = removedStoreDirs;
    }

    public IReadOnlyList<string> RemovedStoreDirs { get; }

    private static string BuildMessage(IReadOnlyList<string> removedStoreDirs, Exception innerException) =>
        $"Could not update the registry, but {removedStoreDirs.Count} store dir(s) were already removed " +
        $"— removed: unverified: {string.Join(", ", removedStoreDirs)}. " +
        $"Cause: {innerException.GetType().Name}: {innerException.Message}";
}
