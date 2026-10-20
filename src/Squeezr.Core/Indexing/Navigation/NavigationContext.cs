using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Navigation;

public static class NavigationContext
{
    public const string MissingIndexMessage = RemedyText.IndexNotBuilt;

    public static string RequireIndex(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        if (!File.Exists(SqueezrPaths.TrigramIndexPath(root)))
        {
            throw new WorkspaceException(MissingIndexMessage, WorkspaceException.IndexNotBuiltCode);
        }

        return root;
    }

    public static string ResolveRepoRoot(string workspaceRoot, RepoEntry repo) =>
        Path.GetFullPath(Path.IsPathRooted(repo.Path) ? repo.Path : Path.Combine(workspaceRoot, repo.Path));

    public static IReadOnlyList<RepoEntry> SelectRepos(WorkspaceManifest manifest, string? repoName)
    {
        var repos = repoName is null
            ? manifest.Repos
            : manifest.Repos.Where(r => string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (repos.Count == 0)
        {
            throw new WorkspaceException($"Repository '{repoName}' is not in the workspace.");
        }

        return repos;
    }
}
