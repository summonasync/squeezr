using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp;

// The startup answer to "which store did this session bind, and why". serve binds by the process working
// directory, so the directory is half the fact and the rule that picked the store is the other half: an agent
// that opened a session in a lane checkout is being answered by whichever store the walk found, and it has to
// learn that before its first query rather than from a cross-tree note that only rides an answer that already
// exists. The names are the ones `worktree resolve' prints — rootPath, branch, storeKey — so one checkout is
// never described two ways.
public static class ServeBindingLine
{
    public static void Write(WorkspaceResolution resolution, string launchDirectory, TextWriter stderr)
    {
        var answersForItself = resolution.IsWorkspace
            && WorkspaceCanonicalPath.SamePhysicalPath(launchDirectory, resolution.RootPath);
        stderr.WriteLine(
            $"squeezr serve: workspace '{resolution.RootPath}' store {StoreOf(resolution)} " +
            $"({Reason(resolution, launchDirectory, answersForItself)})");

        // The git facts are read only when the answering store is not the directory the session stands in, so an
        // ordinary start pays no extra read for this line.
        if (resolution.IsWorkspace && !answersForItself &&
            WorktreeFacts.Resolve(launchDirectory) is { } checkout &&
            !WorkspaceCanonicalPath.SamePhysicalPath(checkout.Toplevel, resolution.RootPath))
        {
            // The shape this feature was opened for: a session inside a checkout that has no store of its own,
            // answered by another tree's index. It is named here rather than left to be inferred, and only for a
            // real checkout — a scratch subdirectory under an indexed root is not a lane waiting to be registered.
            stderr.WriteLine(
                $"warning: this checkout '{checkout.Toplevel}' {HeadOf(checkout)}has no store of its own — " +
                $"every tool here answers from '{resolution.RootPath}' store {StoreOf(resolution)}, where code " +
                $"that lives only on this branch reads as missing; run {RemedyText.WorktreeInitCommand} " +
                "in this directory for an index of this branch");
        }
    }

    private static string StoreOf(WorkspaceResolution resolution) =>
        resolution.StoreKey is { } key ? $"'{key}'" : "none";

    // The no-store case names the walk it ran instead of a store, because that is the fact a reader can act on:
    // serve keeps answering with an error envelope here, and the reason says which directory it looked at.
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
