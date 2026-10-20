namespace Squeezr.Core.Workspace;

public sealed class WorkspaceLockedException : WorkspaceException
{
    public const string LockedCode = "workspace_locked";

    public WorkspaceLockedException()
        : base("Workspace is locked by another squeezr process (index or serve is running).", LockedCode)
    {
    }

    public WorkspaceLockedException(string message, Exception? innerException = null)
        : base(message, LockedCode, innerException)
    {
    }
}
