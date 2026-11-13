using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// The baseline half of `worktree init' as the wire carries it: which repo name joined the store, which checkout
// it points at, and the cost sentence that went out BEFORE the pass that pays it. The notice rides the payload
// because a reader of --json has to be able to check that the price was named and not only paid — a report that
// said only 'baseline: registered' without the sentence it promised first would let the ordering this flag ships
// under go unmeasured.
public sealed record WorktreeBaselineReport(
    string State,
    string RepoName,
    string? BaselineRootPath,
    string LaneRootPath,
    string? CostNotice,
    string? Reason)
{
    public static WorktreeBaselineReport From(WorktreeBaselineOutcome outcome) =>
        new(outcome.State.ToString(), outcome.RepoName, outcome.BaselineRootPath, outcome.LaneRootPath,
            outcome.CostNotice, outcome.Reason);
}
