using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Watch;

public sealed class WatchEventFilter
{
    private static readonly string[] AlwaysIgnoredDirs = [".git", ".squeezr"];
    private static readonly string[] IgnoreFileNames = [".gitignore", ".squeezrignore"];

    private readonly string _storeDir;
    private readonly IReadOnlyList<string> _repoRoots;
    private readonly IReadOnlyDictionary<string, IgnoreRuleset> _rulesByRepo;

    public WatchEventFilter(
        string workspaceRoot,
        IReadOnlyList<string> repoRoots,
        IReadOnlyDictionary<string, IgnoreRuleset> rulesByRepo)
    {
        _storeDir = Path.GetFullPath(SqueezrPaths.StoreDir(workspaceRoot));
        _repoRoots = repoRoots.OrderByDescending(root => root.Length).ToList();
        _rulesByRepo = rulesByRepo;
    }

    public IReadOnlyList<string> RepoRoots => _repoRoots;

    public static bool IsIgnoreFileChange(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        return IgnoreFileNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsGitInternalChange(string fullPath) =>
        fullPath.Split('/', '\\').Contains(".git", StringComparer.OrdinalIgnoreCase);

    internal static bool IsUnderPath(string fullPath, string root) =>
        fullPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)
        || string.Equals(fullPath, root, PathComparison);

    // routing by the layout role, exact extension only: the catalog labels content, this list decides
    // what must wake the watcher, and the two were drifting apart when .slnx/.props/.targets edits went silent
    private static bool IsProjectLayoutFile(string fullPath) =>
        WorkspaceIndexer.ProjectLayoutExtensions.Contains(Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase);

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

        if (_rulesByRepo.TryGetValue(repoRoot, out var rules)
            && rules.Decision(relativePath, isDirectory: false).Excluded)
        {
            return false;
        }

        return pathMayBeGone
            || IsProjectLayoutFile(fullPath)
            || LanguageDetector.Detect(fullPath) != "unknown";
    }

    private bool IsInsideStore(string fullPath) => IsUnderPath(fullPath, _storeDir);

    private string? FindRepoRoot(string fullPath) =>
        _repoRoots.FirstOrDefault(root => IsUnderPath(fullPath, root));

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
