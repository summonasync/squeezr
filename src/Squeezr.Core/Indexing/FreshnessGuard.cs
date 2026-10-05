using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

public sealed record FreshnessResult(IReadOnlyList<string> Refreshed, IReadOnlyList<string> Stale);

public static class FreshnessGuard
{
    public static FreshnessResult CheckAndRefresh(
        string workspaceRoot,
        IReadOnlyList<(string Repo, string Path)> candidates,
        Action<string>? onStaleTakeover = null)
    {
        var refreshed = new List<string>();
        var stale = new List<string>();
        var guard = SecretGuardIngestion.Load(workspaceRoot);

        WorkspaceManifest manifest;
        try
        {
            manifest = WorkspaceManifestFile.Load(workspaceRoot);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return new FreshnessResult([], candidates.Select(Key).ToList());
        }

        foreach (var candidate in candidates)
        {
            try
            {
                // a protected path is never re-read from here: the row it would refresh may only exist in a
                // store that predates the guard, and this route is how such a secret comes straight back.
                // A skip, not a withholding — the tally belongs to the pass that refuses entry.
                if (guard.IsProtected(candidate.Path))
                {
                    continue;
                }

                if (IsFresh(workspaceRoot, manifest, candidate))
                {
                    continue;
                }

                new WorkspaceIndexer().IndexFile(
                    workspaceRoot, candidate.Repo, candidate.Path, onStaleTakeover: onStaleTakeover);
                refreshed.Add(Key(candidate));
            }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                stale.Add(Key(candidate));
            }
        }

        return new FreshnessResult(refreshed, stale);
    }

    private static bool IsFresh(
        string workspaceRoot,
        WorkspaceManifest manifest,
        (string Repo, string Path) candidate)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot));
        var entry = manifest.Repos.FirstOrDefault(r =>
            string.Equals(r.Name, candidate.Repo, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return false;
        }

        var repoDoc = store.FindRepo(candidate.Repo);
        if (repoDoc is null)
        {
            return false;
        }

        var doc = store.ListFiles(repoDoc.Id)
            .FirstOrDefault(f => string.Equals(f.Path, candidate.Path, StringComparison.Ordinal));
        if (doc is null)
        {
            return false;
        }

        var fullPath = Path.Combine(
            NavigationContext.ResolveRepoRoot(workspaceRoot, entry),
            candidate.Path.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return false;
        }

        if (new FileInfo(fullPath).Length != doc.SizeBytes)
        {
            return false;
        }

        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fullPath)))
            .ToLowerInvariant();
        return sha256 == doc.Sha256;
    }

    private static bool IsRecoverable(Exception ex) =>
        ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException;

    private static string Key((string Repo, string Path) candidate) => $"{candidate.Repo}/{candidate.Path}";
}
