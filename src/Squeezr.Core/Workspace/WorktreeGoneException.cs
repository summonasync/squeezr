namespace Squeezr.Core.Workspace;

// The refusal a vanished tree earns. `worktree drop` has made it since T8; `worktree resolve` makes the same
// one because the fifth AC of this card is T8's own acceptance item arriving the day the read verb exists — the
// check that the read door reports a gone tree with the SAME facts, not a paraphrase of them.
//
// The facts ride as a field so the --json envelope can serve them as data: the difference between a structured
// refusal and a sentence that merely mentions one is whether a reader gets the surviving store keys as fields.
// Each verb keeps its own remedy text, because only the door that was asked to act can say what it would have
// done, and a shared sentence would make resolve promise a drop it does not perform.
public sealed class WorktreeGoneException : WorkspaceException
{
    private WorktreeGoneException(WorktreeGoneTree facts, string message)
        : base(message, WorktreeNotRegisteredCode)
    {
        Facts = facts;
    }

    public WorktreeGoneTree Facts { get; }

    public static WorktreeGoneException ForDrop(WorktreeGoneTree facts) =>
        new(facts, facts.Rows.Count > 0
            ? $"{Unmeasurable(facts)}. This verb drops a live tree; reap the row and its store with " +
              $"{RemedyText.WorkspaceRepairCommand}, or clear one row directly with " +
              $"{RemedyText.DropStore(facts.Rows[0].StoreKey)}."
            : $"'{facts.Path}' does not exist and no registry row names it — nothing to drop. If a workspace " +
              $"store outlived this directory, reap it with {RemedyText.WorkspaceRepairCommand}.");

    // Both read doors give the same answer for a gone tree, and they may give one answer because the answer is
    // not about the door: it is about the rows that outlived the directory. Nothing is promised here that a
    // reader did not ask for, which is why the drop sentence cannot be shared and this one can.
    public static WorktreeGoneException ForReadVerb(WorktreeGoneTree facts) =>
        new(facts, facts.Rows.Count > 0
            ? $"{Unmeasurable(facts)}. This verb reads and writes nothing, so those rows, their stores and their " +
              $"collections are exactly where they were; reap them with {RemedyText.WorkspaceRepairCommand}, or "
              + $"clear one row directly with {RemedyText.DropStore(facts.Rows[0].StoreKey)}."
            : $"'{facts.Path}' does not exist and no registry row names it — nothing here resolves to a store, " +
              "and this verb created no row, no directory and no store to point at.");

    private static string Unmeasurable(WorktreeGoneTree facts) =>
        $"'{facts.Path}' does not exist, so no git facts can be read from it, while {facts.Rows.Count} " +
        $"registry row(s) still name it ({Named(facts)})";

    private static string Named(WorktreeGoneTree facts) =>
        string.Join(", ", facts.Rows.Select(row => $"'{row.StoreKey}'"));
}
