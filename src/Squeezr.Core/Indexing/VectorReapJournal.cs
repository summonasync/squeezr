using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing;

// The reap journal is meta.db content, so queueing into it is core storage work — but the pass that
// knows the points could not be removed is the phase, which is why the defer is an internal door the
// implementing assembly shares through InternalsVisibleTo.
internal static class VectorReapJournal
{
    internal static int Defer(
        MetaStore store,
        IReadOnlyList<PendingReapTarget> files,
        List<string> warnings,
        string reason,
        SecretGuardIngestion guard)
    {
        try
        {
            store.EnqueuePendingReaps(WithoutProtectedPaths(files, guard));
            warnings.Add($"{reason}; point cleanup for {files.Count} file(s) deferred to a later pass");
            return files.Count;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            warnings.Add(
                $"{reason}; point cleanup for {files.Count} file(s) NOT deferred " +
                $"(journal write failed: {ex.Message}) — those points stay orphaned");
            return 0;
        }
    }

    // The journal is meta.db content like any other row, so a queued cleanup for a protected path would keep
    // that name in the store bytes after the file row itself is gone. The id is what replay deletes by; the
    // name only proves the id still belongs to the file it was queued for, and "no live row behind the id" is
    // exactly what a purged file is, so the point still gets reaped.
    internal static IReadOnlyList<PendingReapTarget> WithoutProtectedPaths(
        IReadOnlyList<PendingReapTarget> files,
        SecretGuardIngestion guard)
    {
        if (!files.Any(f => f.Path is not null && guard.IsProtected(f.Path)))
        {
            return files;
        }

        return [.. files.Select(f => f.Path is not null && guard.IsProtected(f.Path) ? f with { Path = null } : f)];
    }
}
