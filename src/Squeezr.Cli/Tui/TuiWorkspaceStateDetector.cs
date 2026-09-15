using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Tui;

public static class TuiWorkspaceStateDetector
{
    public static TuiWorkspaceState Detect(string workspaceRoot)
    {
        var resolution = WorkspaceRootResolver.Resolve(workspaceRoot);
        if (!resolution.IsWorkspace)
        {
            return TuiWorkspaceState.Uninitialized;
        }

        return new WorkspaceManager(resolution.RootPath).ListRepos().Count == 0
            ? TuiWorkspaceState.NoRepositories
            : TuiWorkspaceState.Ready;
    }
}
