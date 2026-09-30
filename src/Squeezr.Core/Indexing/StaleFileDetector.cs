using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

public static class StaleFileDetector
{
    public static IReadOnlyList<string> Detect(string workspaceRoot)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            throw new WorkspaceException(
                "Workspace is not initialized. Run 'squeezr init' first.",
                WorkspaceException.NotInitializedCode);
        }

        var manifest = WorkspaceManifestFile.Load(workspaceRoot);
        var config = ConfigFile.Load(workspaceRoot);
        var stale = new List<string>();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot));
        foreach (var repo in manifest.Repos)
        {
            var repoDoc = store.FindRepo(repo.Name);
            IReadOnlyList<FileDocument> dbFiles = repoDoc is null ? [] : store.ListFiles(repoDoc.Id);
            var repoRoot = NavigationContext.ResolveRepoRoot(workspaceRoot, repo);
            if (!Directory.Exists(repoRoot))
            {
                stale.AddRange(dbFiles.Select(f => Key(repo.Name, f.Path)));
                continue;
            }

            var rules = IgnoreRuleset.For(repoRoot, config);
            var walk = RepoFileWalker.Walk(repoRoot, rules, config.Indexing.MaxFileSizeKb);
            var dbByPath = dbFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var walkedPaths = walk.Files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);

            stale.AddRange(walk.Files
                .Where(f => !dbByPath.TryGetValue(f.RelativePath, out var doc) || doc.Sha256 != f.Sha256)
                .Select(f => Key(repo.Name, f.RelativePath)));
            stale.AddRange(dbFiles
                .Where(f => !walkedPaths.Contains(f.Path))
                .Select(f => Key(repo.Name, f.Path)));
        }

        stale.Sort(StringComparer.Ordinal);
        return stale;
    }

    private static string Key(string repoName, string path) => $"{repoName}/{path}";
}
