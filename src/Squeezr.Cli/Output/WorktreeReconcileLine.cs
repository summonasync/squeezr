using Spectre.Console;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// The fill's counterpart line: the copy says what landed, this says whether the lane now answers for its own
// tree. Three shapes carry words, one carries numbers, and 'reconcile pending' stays the phrase the copied
// lane is reported with, because the session-start reader that has to react to it reacts to pending, not to
// which of the four reasons produced it.
internal static class WorktreeReconcileLine
{
    internal static string Build(WorktreeReconcileReport reconcile)
    {
        var reason = reconcile.Reason is { } text ? $" — {text.EscapeMarkup()}" : "";
        return reconcile.State switch
        {
            nameof(WorktreeReconcileState.Deferred) => $"  reconcile [grey]deferred[/]{reason}",
            nameof(WorktreeReconcileState.Reconciled) when reconcile.PassRan =>
                $"  reconcile [green]reconciled[/] — {PassText(reconcile)}",
            nameof(WorktreeReconcileState.Reconciled) =>
                $"  reconcile [green]reconciled[/] — this store already answers for its own rows "
                + $"({reconcile.StoredFiles} file(s), {CollectionText(reconcile)}); no pass was owed",
            _ => $"  reconcile [yellow]pending[/] — {(reconcile.PassRan ? PassText(reconcile) + "; " : "")}"
                + $"{CollectionText(reconcile)}{reason}",
        };
    }

    private static string PassText(WorktreeReconcileReport reconcile) =>
        $"pass over this tree: {reconcile.RescannedFiles} file(s) rescanned, "
        + $"+{reconcile.Added} ~{reconcile.Modified} -{reconcile.Deleted} "
        + $"({reconcile.Skipped} skipped), re-embedded {reconcile.Reembedded} chunk(s)";

    private static string CollectionText(WorktreeReconcileReport reconcile) =>
        // A store whose rows claim no vector has no collection question to answer; the pending chunks are the
        // embedding queue, which is 'status' business and not a reconcile that fell short.
        reconcile.Obligation
            ? $"collection {reconcile.CollectionPoints?.ToString() ?? "no answer"} point(s) against "
                + $"{reconcile.StoredChunks - reconcile.PendingChunks} row(s) this store calls embedded"
            : $"{reconcile.StoredChunks - reconcile.PendingChunks} row(s) call themselves embedded, "
                + $"{reconcile.PendingChunks} chunk(s) wait for embeddings";
}
