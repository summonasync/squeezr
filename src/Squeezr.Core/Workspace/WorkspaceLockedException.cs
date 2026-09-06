namespace Squeezr.Core.Workspace;

public sealed class WorkspaceLockedException : WorkspaceException
{
    public WorkspaceLockedException()
        : base("Workspace is locked by another squeezr process (index or serve is running).")
    {
    }
}
