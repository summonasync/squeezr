using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Tui;

public static class TuiWorkspaceStateDetector
{
    public static TuiWorkspaceState Detect(string workspaceRoot)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            return TuiWorkspaceState.Uninitialized;
        }

        return new WorkspaceManager(workspaceRoot).ListRepos().Count == 0
            ? TuiWorkspaceState.NoRepositories
            : TuiWorkspaceState.Ready;
    }
}
