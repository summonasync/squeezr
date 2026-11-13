using System.Text.Json.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// What the read verb answers, in the shape a session hook consumes: the checkout the path sits in, every
// registry row that names it, and what each of those rows' stores says about itself. The state is the first
// field because it is the one a reader branches on, and the reason rides with it so a 'nothing here' answer says
// which nothing it found.
//
// The GONE answer is carried here too, with the same fields `worktree drop' refuses on: this block is the
// structured half of that refusal, while the stderr envelope keeps the code and the sentence, because a reader
// that has to parse prose to learn which store keys still claim a dead directory is reading a sentence and not
// an answer. The key is absent on every other state, so nothing about a healthy answer's shape changes (035).
public sealed record WorktreeResolveReport(
    string State,
    string Path,
    string PhysicalPath,
    string? RootPath,
    string? Branch,
    bool IsLinked,
    bool IsDetached,
    string? ParentRootPath,
    bool ParentRegistered,
    int DependentLanes,
    IReadOnlyList<string> StoreKeys,
    IReadOnlyList<WorktreeResolveRow> Rows,
    IReadOnlyList<string> Notices,
    bool? ReconcilePending,
    string? Reason,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WorktreeGoneTree? Gone)
{
    public static WorktreeResolveReport From(WorktreeResolveOutcome outcome)
    {
        var rows = outcome.Rows.Select(WorktreeResolveRow.Of).ToList();
        return new WorktreeResolveReport(
            outcome.State.ToString(),
            outcome.Path,
            outcome.PhysicalPath,
            outcome.RootPath,
            outcome.Branch,
            outcome.IsLinked,
            outcome.IsDetached,
            outcome.ParentRootPath,
            outcome.ParentRegistered,
            outcome.DependentLanes,
            [.. rows.Select(row => row.StoreKey)],
            rows,
            [.. rows.SelectMany(row => row.Notices)],
            Fold(rows),
            outcome.Reason,
            null);
    }

    public static WorktreeResolveReport FromGone(WorktreeGoneException refusal) =>
        new(
            WorktreeResolveState.Gone.ToString(),
            refusal.Facts.Path,
            refusal.Facts.PhysicalPath,
            null,
            null,
            false,
            false,
            null,
            false,
            0,
            [.. refusal.Facts.Rows.Select(row => row.StoreKey)],
            [],
            [],
            null,
            refusal.Message,
            refusal.Facts);

    // A tree with two spellings has two stores, and the pending question is answered by both: one says pending if
    // either does. No record at all is neither 'pending' nor 'settled' — it is the absence that reads as
    // nothing-pending under the additive rule, so it stays null and the key stays off the wire.
    private static bool? Fold(IReadOnlyList<WorktreeResolveRow> rows)
    {
        var records = rows.Select(row => row.Record).Where(record => record is not null).ToList();
        return records.Count == 0 ? null : records.Any(record => record!.Pending);
    }
}
