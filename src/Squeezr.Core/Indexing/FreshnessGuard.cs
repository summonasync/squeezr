using System.Diagnostics;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

// Unrefreshed is the budget's own tally: every key it holds is also in Stale, and it names the part of that
// list this call refused because the budget was gone. The early returns below hand back an empty
// Unrefreshed on purpose — a call that never reached the candidates left nothing behind at a budget, it
// reached nothing, and the reason a caller reads must not be allowed to collapse into the budget shape.
// A cancel before the first batch starts joins them: it stopped the call before anything was spent, so
// every candidate is Stale and none of it is a refusal.
public sealed record FreshnessResult(
    IReadOnlyList<string> Refreshed,
    IReadOnlyList<string> Stale,
    IReadOnlyList<string> Unrefreshed);

public static class FreshnessGuard
{
    // the cap is a contract with the read surfaces, not a tuning knob: raising it buys a linear
    // price hike on every response and a wider leak window for the protected-path tally
    public const int CandidateLimit = 50;

    public static FreshnessResult CheckAndRefresh(
        string workspaceRoot,
        IReadOnlyList<(string Repo, string Path)> candidates,
        Action<string>? onStaleTakeover = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();
        return CheckAndRefresh(
            workspaceRoot,
            candidates,
            onStaleTakeover,
            // IndexScoped takes the store lock bare, so the stale-takeover notice has no slot here: the
            // takeover still happens inside the batch, only the stderr line is gone from this route —
            // recorded as an obstacle on 032/T4, where the probe alternative loses to a second, racing
            // copy of IndexLock's ownership decision
            (root, batch, takeover) => new WorkspaceIndexer().IndexScoped(
                root, [.. batch.Select(candidate => candidate.AbsolutePath)]),
            () => clock.Elapsed,
            cancellationToken);
    }

    internal static FreshnessResult CheckAndRefresh(
        string workspaceRoot,
        IReadOnlyList<(string Repo, string Path)> candidates,
        Action<string>? onStaleTakeover,
        ReindexScoped reindex,
        Func<TimeSpan> elapsed,
        CancellationToken cancellationToken = default)
    {
        var refreshed = new List<string>();
        var stale = new List<string>();
        var unrefreshed = new List<string>();
        var guard = SecretGuardIngestion.Load(workspaceRoot);

        WorkspaceManifest manifest;
        try
        {
            manifest = WorkspaceManifestFile.Load(workspaceRoot);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return new FreshnessResult([], candidates.Select(Key).ToList(), []);
        }

        var store = OpenStore(workspaceRoot);
        if (store is null)
        {
            // candidates used to fail this one at a time, each open throwing inside its own probe:
            // every non-protected path goes to stale and nothing is refreshed
            return new FreshnessResult(
                [], candidates.Where(c => !guard.IsProtected(c.Path)).Select(Key).ToList(), []);
        }

        using (store)
        {
            var sources = new Dictionary<string, CandidateSource?>(StringComparer.Ordinal);
            var announced = new List<(string Repo, string Path, string AbsolutePath)>();
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

                    // a repo the manifest or the store does not name leaves no root to announce a path
                    // under: the candidate goes to stale here, the verdict the per-file route used to
                    // deliver by throwing the same resolution as a WorkspaceException
                    var source = ResolveSource(manifest, store, sources, candidate.Repo);
                    if (source is null)
                    {
                        stale.Add(Key(candidate));
                        continue;
                    }

                    if (CameToAgreement(workspaceRoot, store, source, candidate))
                    {
                        continue;
                    }

                    // the deterministic point: a slot the batch has not taken yet is the only thing the
                    // budget can refuse, and it is refused as itself, never as a check that came back bad.
                    // The first slot is never refused — a call that found a stale file heals at least one —
                    // so the wall bound reads as "take another", and a slow store open cannot empty the
                    // call. Cancellation is read here and nowhere else, so a call stops between acceptance
                    // and batch, never inside one: before its first acceptance it reached nothing and
                    // collapses like the workspace failures above, after one it refuses the remainder the
                    // way the budget does
                    if (announced.Count >= RefreshBudget.MaxFilesReRead ||
                        (announced.Count > 0 && elapsed().TotalMilliseconds >= RefreshBudget.MaxWallClockMillis) ||
                        cancellationToken.IsCancellationRequested)
                    {
                        if (cancellationToken.IsCancellationRequested && announced.Count == 0)
                        {
                            return new FreshnessResult([], candidates.Select(Key).ToList(), []);
                        }

                        var refused = Key(candidate);
                        stale.Add(refused);
                        unrefreshed.Add(refused);
                        continue;
                    }

                    announced.Add(
                        (candidate.Repo, candidate.Path, RepoFilePath(workspaceRoot, source.Entry, candidate.Path)));
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    stale.Add(Key(candidate));
                }
            }

            if (announced.Count > 0)
            {
                List<string> healed;
                List<string> unhealed;
                try
                {
                    (healed, unhealed) = MapBatchOutcome(
                        workspaceRoot,
                        store,
                        sources,
                        announced,
                        reindex(workspaceRoot, announced, onStaleTakeover));
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    healed = [];
                    unhealed = [.. announced.Select(c => Key((c.Repo, c.Path)))];
                }

                refreshed.AddRange(healed);
                stale.AddRange(unhealed);
            }
        }

        return new FreshnessResult(refreshed, stale, unrefreshed);
    }

    // the batch answers in totals, and totals cannot name the path a skip cost. When Indexed and Removed
    // reconcile with the announced size, every announced path was processed — written or reaped — and both
    // are the healed verdict this route reports. On a shortfall the totals go quiet and each candidate
    // answers from the state the batch left behind: a row that came to agreement names a healed candidate,
    // anything else stays stale, and nothing is refreshed on the counts' say-so alone.
    private static (List<string> Healed, List<string> Unhealed) MapBatchOutcome(
        string workspaceRoot,
        MetaStore store,
        Dictionary<string, CandidateSource?> sources,
        IReadOnlyList<(string Repo, string Path, string AbsolutePath)> announced,
        WorkspaceIndexer.ScopedIndexResult outcome)
    {
        if (outcome.Indexed + outcome.Removed == announced.Count)
        {
            return ([.. announced.Select(c => Key((c.Repo, c.Path)))], []);
        }

        List<string> healed = [];
        List<string> unhealed = [];
        foreach (var candidate in announced)
        {
            var source = sources[candidate.Repo]!;
            if (CameToAgreement(workspaceRoot, store, source, (candidate.Repo, candidate.Path)))
            {
                healed.Add(Key((candidate.Repo, candidate.Path)));
            }
            else
            {
                unhealed.Add(Key((candidate.Repo, candidate.Path)));
            }
        }

        return (healed, unhealed);
    }

    // Agreement is one predicate on both sides of the batch: before it, agreement is the freshness check —
    // an agreed candidate is not a candidate; after a shortfall, agreement is the healed verdict — the row
    // the batch wrote matches the file it named, and a path it reaped keeps no row behind no readable file.
    // The path shape is probed before any content read, so an unreadable or non-regular file is judged on
    // presence, never opened.
    private static bool CameToAgreement(
        string workspaceRoot,
        MetaStore store,
        CandidateSource source,
        (string Repo, string Path) candidate)
    {
        var doc = store.FindFile(source.RepoId, candidate.Path);
        // the path index answers case-insensitively (LiteDB collation), the scan this lookup replaced
        // filtered Ordinal — without this guard a repo holding both 'a.cs' and 'A.cs' with identical
        // content could be called fresh against the other file's row and never get its own row written
        var namedRow = doc is not null && string.Equals(doc.Path, candidate.Path, StringComparison.Ordinal);
        var fullPath = RepoFilePath(workspaceRoot, source.Entry, candidate.Path);

        if (!File.Exists(fullPath) || !UnixFileType.IsRegularFile(fullPath))
        {
            return !namedRow;
        }

        if (!namedRow || new FileInfo(fullPath).Length != doc!.SizeBytes)
        {
            return false;
        }

        var sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(fullPath)))
            .ToLowerInvariant();
        return sha256 == doc.Sha256;
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

    private static string RepoFilePath(string workspaceRoot, RepoEntry entry, string relativePath) =>
        Path.Combine(
            NavigationContext.ResolveRepoRoot(workspaceRoot, entry),
            relativePath.Replace('/', Path.DirectorySeparatorChar));

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
