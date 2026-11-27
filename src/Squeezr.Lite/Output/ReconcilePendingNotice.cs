using Squeezr.Core.Workspace;

namespace Squeezr.Lite.Output;

// Same reading of the store's reconcile record as the full product's status surfaces, re-worded for the lite
// verbs: a copied store that no pass has proved still says so. The record's vector-accounting branches are the
// semantic profile's story and have no seat here — a lite session reads no vectors, so their absence is not
// pending work this door could close.
internal static class ReconcilePendingNotice
{
    internal static WorktreeRecord? Read(string workspaceRoot) =>
        WorktreeRecord.Of(WorkspaceManifestFile.TryRead(workspaceRoot));

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
                + "holds no copied rows. Re-run 'squeezr worktree init' here to fill it, or "
                + $"'{LiteRemedy.IndexCommand}' to index this tree into it.");
        }
        else if (record.Code == ReconcileMarker.BaselineWidenedCode)
        {
            lines.Add(
                $"reconcile pending: repo '{WorktreeBaseline.RepoName}' joined this store after its last pass "
                + "proved the rows the manifest owned then, so the second tree's coordinates are unmeasured here. "
                + "Re-run 'squeezr worktree init' to pass over both trees, or "
                + $"'{LiteRemedy.IndexCommand}' to index them into the store.");
        }
        else if (!record.PassCompleted)
        {
            lines.Add(
                "reconcile pending: the rows in this store were copied from the main worktree, and no pass "
                + "over THIS tree has proved them — file paths, line numbers and symbols here still describe "
                + "that checkout. Re-run 'squeezr worktree init' to run the pass, or "
                + $"'{LiteRemedy.IndexCommand}' to index this tree into the store.");
        }

        if (record.PassCompleted && record.ExactArmLanded == false)
        {
            lines.Add(
                "this store has no trigram index, so exact search answers nothing here while the rest does: the main "
                + "worktree held no trigram.idx when the copy ran, and a lane builds its own only when a pass over "
                + $"its tree indexes it ('{LiteRemedy.IndexCommand}').");
        }

        return lines;
    }
}
