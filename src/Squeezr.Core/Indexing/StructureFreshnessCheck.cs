using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing;

// One pass over the indexed rows of the scope a map answer renders, answered without opening any file:
// existence and file type from stat, change from size or mtime against the row's own stamp. A content-exact
// answer is the hash guard's; this one is about structure and says so through its counts. Nothing here
// writes — an inline refresh turns one dirty file into a full index pass on the caller's thread.
public sealed record StructureFreshnessResult(
    int Checked,
    int Withheld,
    int Skipped,
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Changed)
{
    // both classes mean "the index no longer describes this row", which is what the envelope's staleFiles
    // list carries today; the split stays visible to the caller because the two claims are not the same
    public IReadOnlyList<string> Stale => [.. Missing, .. Changed];
}

public static class StructureFreshnessCheck
{
    private static readonly StructureFreshnessResult Unexamined =
        new(0, 0, 0, [], []);

    public static StructureFreshnessResult Check(
        string workspaceRoot,
        IReadOnlyList<string> repos,
        string? prefix = null)
    {
        try
        {
            return CheckCore(workspaceRoot, repos, prefix);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // a pass that could not run reports zero coverage rather than a clean sheet: every number in
            // the result is a claim about rows actually looked at
            return Unexamined;
        }
    }

    private static StructureFreshnessResult CheckCore(
        string workspaceRoot,
        IReadOnlyList<string> repos,
        string? prefix)
    {
        var normalizedPrefix = PathPrefix.Normalize(prefix);
        var manifest = WorkspaceManifestFile.Load(workspaceRoot);
        var guard = SecretGuardReader.Load(workspaceRoot);
        var store = MetaStore.TryOpen(SqueezrPaths.MetaDbPath(workspaceRoot));
        if (store is null)
        {
            return Unexamined;
        }

        using (store)
        {
            var checkedRows = 0;
            var withheld = 0;
            var skipped = 0;
            var missing = new List<string>();
            var changed = new List<string>();

            foreach (var repoName in repos)
            {
                var entry = manifest.Repos.FirstOrDefault(r =>
                    string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase));
                var repoDoc = store.FindRepo(repoName);
                if (repoDoc is null)
                {
                    continue;
                }

                if (entry is null)
                {
                    skipped += store.CountFiles(repoDoc.Id);
                    continue;
                }

                var repoRoot = NavigationContext.ResolveRepoRoot(workspaceRoot, entry);
                foreach (var doc in store.ListFiles(repoDoc.Id))
                {
                    if (normalizedPrefix is not null && !PathPrefix.Matches(normalizedPrefix, doc.Path))
                    {
                        continue;
                    }

                    // a protected row is never named and never probed: its path is the answer the guard
                    // exists to withhold, and a verdict list is a path list
                    if (guard.IsProtected(doc.Path))
                    {
                        withheld++;
                        continue;
                    }

                    checkedRows++;
                    var key = $"{repoName}/{doc.Path}";
                    switch (Inspect(repoRoot, doc))
                    {
                        case RowState.Missing:
                            missing.Add(key);
                            break;
                        case RowState.Changed:
                            changed.Add(key);
                            break;
                    }
                }
            }

            return new StructureFreshnessResult(checkedRows, withheld, skipped, missing, changed);
        }
    }

    private enum RowState
    {
        Fresh,
        Missing,
        Changed,
    }

    private static RowState Inspect(string repoRoot, FileDocument doc)
    {
        var fullPath = Path.Combine(repoRoot, doc.Path.Replace('/', Path.DirectorySeparatorChar));
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            return RowState.Missing;
        }

        // a channel or a socket answers stat like a file and reads like a hang; the row it replaced
        // describes a regular file that is no longer there
        if (!UnixFileType.IsRegularFile(fullPath))
        {
            return RowState.Missing;
        }

        var modified = AsUtc(info.LastWriteTimeUtc);
        return info.Length != doc.SizeBytes || modified > AsUtc(doc.IndexedAt)
            ? RowState.Changed
            : RowState.Fresh;
    }

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();

    private static bool IsRecoverable(Exception ex) =>
        ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException;
}
