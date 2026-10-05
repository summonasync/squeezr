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
        var guard = SecretGuardIngestion.Load(workspaceRoot);
        foreach (var repo in manifest.Repos)
        {
            var repoDoc = store.FindRepo(repo.Name);
            IReadOnlyList<FileDocument> dbFiles = repoDoc is null ? [] : store.ListFiles(repoDoc.Id);
            var repoRoot = NavigationContext.ResolveRepoRoot(workspaceRoot, repo);
            if (!Directory.Exists(repoRoot))
            {
                // a vanished repo still lists its rows, and a row is a name — the guard does not lapse
                // because the directory behind the store is gone
                stale.AddRange(dbFiles
                    .Where(f => !guard.IsProtected(f.Path))
                    .Select(f => Key(repo.Name, f.Path)));
                continue;
            }

            var rules = IgnoreRuleset.For(repoRoot, config);
            // staleness reads Files only; counting the pruned subtrees would walk 30k+ paths nobody reports
            var walk = RepoFileWalker.Walk(
                repoRoot, rules, guard, config.Indexing.MaxFileSizeKb, countIgnoredSubtrees: false);
            var dbByPath = dbFiles.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var walkedPaths = walk.Files.Select(f => f.RelativePath).ToHashSet(StringComparer.Ordinal);

            stale.AddRange(walk.Files
                .Where(f => !dbByPath.TryGetValue(f.RelativePath, out var doc) || doc.Sha256 != f.Sha256)
                .Select(f => Key(repo.Name, f.RelativePath)));
            // a protected row is stale by construction — the pass that meets it drops it — and naming it in a
            // report is the exposure the guard exists to close, so it is counted, not listed
            stale.AddRange(dbFiles
                .Where(f => !walkedPaths.Contains(f.Path) && !guard.IsProtected(f.Path))
                .Select(f => Key(repo.Name, f.Path)));
        }

        stale.Sort(StringComparer.Ordinal);
        return stale;
    }

    private static string Key(string repoName, string path) => $"{repoName}/{path}";
}
