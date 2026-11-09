using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

public sealed record FreshnessResult(IReadOnlyList<string> Refreshed, IReadOnlyList<string> Stale);

public static class FreshnessGuard
{
    // the cap is a contract with the read surfaces, not a tuning knob: raising it buys a linear
    // price hike on every response and a wider leak window for the protected-path tally
    public const int CandidateLimit = 50;

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

        var store = OpenStore(workspaceRoot);
        if (store is null)
        {
            // candidates used to fail this one at a time, each open throwing inside its own probe:
            // every non-protected path goes to stale and nothing is refreshed
            return new FreshnessResult(
                [], candidates.Where(c => !guard.IsProtected(c.Path)).Select(Key).ToList());
        }

        using (store)
        {
            var sources = new Dictionary<string, CandidateSource?>(StringComparer.Ordinal);
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

                    if (IsFresh(workspaceRoot, manifest, store, sources, candidate))
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
        }

        return new FreshnessResult(refreshed, stale);
    }

    private static MetaStore? OpenStore(string workspaceRoot)
    {
        try
        {
            return new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot));
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return null;
        }
    }

    private static bool IsFresh(
        string workspaceRoot,
        WorkspaceManifest manifest,
        MetaStore store,
        Dictionary<string, CandidateSource?> sources,
        (string Repo, string Path) candidate)
    {
        var source = ResolveSource(manifest, store, sources, candidate.Repo);
        if (source is null)
        {
            return false;
        }

        var doc = store.FindFile(source.RepoId, candidate.Path);
        // the path index answers case-insensitively (LiteDB collation), the scan this lookup replaced
        // filtered Ordinal — without this guard a repo holding both 'a.cs' and 'A.cs' with identical
        // content could be called fresh against the other file's row and never get its own row written
        if (doc is null || !string.Equals(doc.Path, candidate.Path, StringComparison.Ordinal))
        {
            return false;
        }

        var fullPath = Path.Combine(
            NavigationContext.ResolveRepoRoot(workspaceRoot, source.Entry),
            candidate.Path.Replace('/', Path.DirectorySeparatorChar));

        // the size comparison below is no filter: a channel reports st_size 0, so it matches an indexed
        // empty row and the hash read would open a writerless FIFO on the caller's thread, outside any lock
        if (!File.Exists(fullPath) || !UnixFileType.IsRegularFile(fullPath))
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

    // a candidate list is repo-homogeneous in practice, so the name → (manifest entry, store row) pair
    // is resolved once per distinct name instead of once per candidate
    private static CandidateSource? ResolveSource(
        WorkspaceManifest manifest,
        MetaStore store,
        Dictionary<string, CandidateSource?> sources,
        string repo)
    {
        if (sources.TryGetValue(repo, out var known))
        {
            return known;
        }

        var entry = manifest.Repos.FirstOrDefault(r =>
            string.Equals(r.Name, repo, StringComparison.OrdinalIgnoreCase));
        var repoDoc = entry is null ? null : store.FindRepo(repo);
        var source = repoDoc is null ? null : new CandidateSource(entry!, repoDoc.Id);
        sources[repo] = source;
        return source;
    }

    private static bool IsRecoverable(Exception ex) =>
        ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException;

    private static string Key((string Repo, string Path) candidate) => $"{candidate.Repo}/{candidate.Path}";

    private sealed record CandidateSource(RepoEntry Entry, uint RepoId);
}
