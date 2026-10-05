using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

// The hook reads the disk, and the disk knows nothing about the guard: a big protected file answered with
// advice while a missing one answered with silence, which is an existence oracle sitting outside the index
// plane. Extras are written relative to a repo root, so the repo roots get asked alongside the workspace one.
internal static class HookSecretPaths
{
    internal static bool IsGuarded(string fullPath, WorkspaceRegistryEntry workspace)
    {
        var guard = SecretGuardReader.Load(workspace.RootPath);
        return Decides(guard, workspace.RootPath, fullPath)
            || RepoRoots(workspace.RootPath).Any(root => Decides(guard, root, fullPath));
    }

    private static IEnumerable<string> RepoRoots(string workspaceRoot)
    {
        try
        {
            return WorkspaceManifestFile.Exists(workspaceRoot)
                ? [.. WorkspaceManifestFile.Load(workspaceRoot).Repos
                    .Select(repo => NavigationContext.ResolveRepoRoot(workspaceRoot, repo))]
                : [];
        }
        catch (WorkspaceException)
        {
            return [];
        }
    }

    private static bool Decides(SecretGuardReader guard, string root, string fullPath) =>
        RelativeUnder(root, fullPath) is { } relative && guard.IsProtected(relative);

    private static string? RelativeUnder(string root, string fullPath)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.StartsWith(trimmed + Path.DirectorySeparatorChar, PathComparison)
            ? fullPath[(trimmed.Length + 1)..]
            : null;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
