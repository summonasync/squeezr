using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Watch;

public sealed class WatchEventFilter
{
    private static readonly string[] AlwaysIgnoredDirs = [".git", ".squeezr"];

    private readonly string _storeDir;
    private readonly IReadOnlyList<string> _repoRoots;
    private readonly IReadOnlyList<string> _ignoreGlobs;

    public WatchEventFilter(string workspaceRoot, IReadOnlyList<string> repoRoots, IReadOnlyList<string> ignoreGlobs)
    {
        _storeDir = Path.GetFullPath(SqueezrPaths.StoreDir(workspaceRoot));
        _repoRoots = repoRoots.OrderByDescending(root => root.Length).ToList();
        _ignoreGlobs = ignoreGlobs;
    }

    public IReadOnlyList<string> RepoRoots => _repoRoots;

    public bool ShouldTrack(string fullPath, bool pathMayBeGone)
    {
        if (IsInsideStore(fullPath))
        {
            return false;
        }

        var repoRoot = FindRepoRoot(fullPath);
        if (repoRoot is null)
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(repoRoot, fullPath).Replace('\\', '/');
        if (relativePath.Split('/').Any(segment => AlwaysIgnoredDirs.Contains(segment, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (_ignoreGlobs.Any(glob => GlobMatcher.IsMatch(glob, relativePath)))
        {
            return false;
        }

        return pathMayBeGone || LanguageDetector.Detect(fullPath) != "unknown";
    }

    private bool IsInsideStore(string fullPath) =>
        fullPath.StartsWith(_storeDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fullPath, _storeDir, StringComparison.OrdinalIgnoreCase);

    private string? FindRepoRoot(string fullPath) =>
        _repoRoots.FirstOrDefault(root =>
            fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
}
