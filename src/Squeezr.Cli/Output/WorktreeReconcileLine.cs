using Spectre.Console;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// The fill's counterpart line: the copy says what landed, this says whether the lane now answers for its own
// tree. Four shapes carry words, one carries numbers, and 'reconcile pending' stays the phrase the copied
// lane is reported with, because the session-start reader that has to react to it reacts to pending, not to
// which of the reasons produced it.
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
                // The proof this line names is the store's own: a pass over this root closed it, and this
                // session start read that record and asked the collection for a spread of the ids it counted.
                // Saying 'this pass proved it' here would credit work another session did.
                $"  reconcile [green]reconciled[/] — on this store's record: a pass over this tree closed "
                + $"the proof{AttestationText(reconcile)}",
            _ => $"  reconcile [yellow]pending[/] — {(reconcile.PassRan ? PassText(reconcile) + "; " : "")}"
                + $"{CollectionText(reconcile)}{reason}",
        };
    }

    private static string AttestationText(WorktreeReconcileReport reconcile) =>
        reconcile.ProbedIds is int probed and > 0
            ? $", attested by {probed} point id(s) the collection answered for"
            : ", with no vector claim on it to attest";

    private static string PassText(WorktreeReconcileReport reconcile) =>
        $"pass over this tree: {reconcile.RescannedFiles} file(s) rescanned, "
        + $"+{reconcile.Added} ~{reconcile.Modified} -{reconcile.Deleted} "
        + $"({reconcile.Skipped} skipped), re-embedded {reconcile.Reembedded} chunk(s)";

    private static string CollectionText(WorktreeReconcileReport reconcile) =>
        // A store whose rows claim no vector has no collection question to answer; the pending chunks are the
        // embedding queue, which is 'status' business and not a reconcile that fell short.
        reconcile.Obligation
            ? $"{reconcile.CoveredChunks?.ToString() ?? "no answer"} covered of {reconcile.ObligationChunks} "
                + $"chunk hash(es) this store calls embedded, collection "
                + $"{reconcile.CollectionPoints?.ToString() ?? "no answer"} point(s)"
            : $"{reconcile.ObligationChunks} chunk hash(es) this store calls embedded, "
                + $"{reconcile.PendingChunks} chunk(s) wait for embeddings";
}
