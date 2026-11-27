using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Lite.Mcp;

public static class ServeBindingLine
{
    public static void Write(WorkspaceResolution resolution, string launchDirectory, TextWriter stderr)
    {
        var answersForItself = resolution.IsWorkspace
            && WorkspaceCanonicalPath.SamePhysicalPath(launchDirectory, resolution.RootPath);
        stderr.WriteLine(
            $"sqzrlite serve: workspace '{resolution.RootPath}' store {StoreOf(resolution)} " +
            $"({Reason(resolution, launchDirectory, answersForItself)})");

        if (resolution.IsWorkspace && !answersForItself &&
            WorktreeFacts.Resolve(launchDirectory) is { } checkout &&
            !WorkspaceCanonicalPath.SamePhysicalPath(checkout.Toplevel, resolution.RootPath))
        {
            stderr.WriteLine(
                $"warning: this checkout '{checkout.Toplevel}' {HeadOf(checkout)}has no store of its own — " +
                $"every tool here answers from '{resolution.RootPath}' store {StoreOf(resolution)}, where code " +
                "that lives only on this branch reads as missing; run 'squeezr worktree init' " +
                "in this directory for an index of this branch");
        }
    }

    private static string StoreOf(WorkspaceResolution resolution) =>
        resolution.StoreKey is { } key ? $"'{key}'" : "none";

    private static string Reason(WorkspaceResolution resolution, string launchDirectory, bool answersForItself) =>
        !resolution.IsWorkspace
            ? $"no registered workspace at or above '{launchDirectory}'"
            : answersForItself
                ? "this directory is a registered workspace root"
                : $"nearest registered workspace above '{launchDirectory}'";

    private static string HeadOf(WorktreeFacts facts) =>
        facts.IsDetached
            ? "(detached head) "
            : facts.Branch is { } branch
                ? $"(branch {branch}) "
                : string.Empty;
}
