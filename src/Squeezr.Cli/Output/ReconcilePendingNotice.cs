using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// The one reading of a store's reconcile record that every reporting surface shares, because 'status',
// 'index_status' and an answer that names files are three doors on the same fact and must not become three
// stories about it.
//
// What the record says is reported as the separate answers it carries, never folded into one word: whether a
// copy ever filled this store, whether the exact arm came with it, and whether a pass over this root has
// closed. A store with no record is not a store that owes a warning — a workspace nobody filled from a main
// worktree has nothing pending in it, and an additive field that is absent reads as the state before the rule
// rather than as a failure of it (035).
internal static class ReconcilePendingNotice
{
    internal static WorktreeRecord? Read(string workspaceRoot) =>
        WorktreeRecord.Of(WorkspaceManifestFile.TryRead(workspaceRoot));

    // The gate a per-file verdict reads: while no pass over this root has proved that these rows are this
    // checkout's, an answer may not say 'fresh' about a file this store has never seen indexed here.
    internal static bool CoordinatesUnproven(WorktreeRecord? record) =>
        record is { PassCompleted: false };

    internal static IReadOnlyList<string> Lines(WorktreeRecord? record)
    {
        if (record is null)
        {
            return [];
        }

        var lines = new List<string>();
        if (!record.Filled)
        {
            lines.Add(
                "reconcile pending: this worktree's store was opened for a fill that never landed, so it "
                + $"holds no copied rows. Re-run '{RemedyText.WorktreeInitCommand}' here to fill it, or "
                + $"'{RemedyText.IndexCommand}' to index this tree into it.");
        }
        else if (record.Code == ReconcileMarker.BaselineWidenedCode)
        {
            // A baseline row is the one event that reopens a closed record without a pass, and it owes its own
            // words: nothing here was copied and left unproven — the store proved one tree and was then handed a
            // second. 'the rows were copied' would describe a different gap and pitch a remedy that reads the
            // wrong field.
            lines.Add(
                $"reconcile pending: repo '{WorktreeBaseline.RepoName}' joined this store after its last pass "
                + "proved the rows the manifest owned then, so the second tree's coordinates are unmeasured here. "
                + $"Re-run '{RemedyText.WorktreeInitCommand}' to pass over both trees, or "
                + $"'{RemedyText.IndexCommand}' to index them into the store.");
        }
        else if (!record.PassCompleted)
        {
            lines.Add(
                "reconcile pending: the rows in this store were copied from the main worktree, and no pass "
                + "over THIS tree has proved them — file paths, line numbers and symbols here still describe "
                + $"that checkout. Re-run '{RemedyText.WorktreeInitCommand}' to run the pass, or "
                + $"'{RemedyText.IndexCommand}' to index this tree into the store.");
        }
        else if (record.SemanticArmLanded != true)
        {
            lines.Add(
                $"reconcile pending: a pass over this tree proved its coordinates, and the vectors its rows "
                + $"claim are not accounted for ({AccountingText(record)}). Start the services with "
                + $"'{RemedyText.ServicesUp}' and re-run '{RemedyText.WorktreeInitCommand}' here, or close "
                + $"the gap with '{RemedyText.VectorsReconcileApply}'.");
        }

        if (record.PassCompleted && record.ExactArmLanded == false)
        {
            lines.Add(ExactArmLine());
        }

        return lines;
    }

    // An accounting this store never took is not an accounting of zero, and the two read differently to the
    // person who has to decide whether to start a service or re-run a pass.
    private static string AccountingText(WorktreeRecord record) =>
        record.CoveredChunks is { } covered
            ? $"{covered} of the {record.ObligationChunks} chunk hash(es) it calls embedded have a point in "
            + $"the collection, code '{record.Code ?? "none"}'"
            : $"the collection never answered for its {record.ObligationChunks} chunk hash(es), and nothing "
            + $"was counted (code '{record.Code ?? "none"}')";

    private static string ExactArmLine() =>
        "this store has no trigram index, so exact search answers nothing here while the rest does: the main "
        + "worktree held no trigram.idx when the copy ran, and a lane builds its own only when a pass over "
        + $"its tree indexes it ('{RemedyText.IndexCommand}').";
}
