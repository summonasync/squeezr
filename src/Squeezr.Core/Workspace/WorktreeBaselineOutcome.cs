namespace Squeezr.Core.Workspace;

// What the baseline half of `worktree init` did, and what it is about to cost. The cost line is produced here,
// with both roots named, so the door that prints it cannot print a cheaper number than the door that measured
// it: a notice about doubling an index that arrives after the pass which doubled it is an explanation, not a
// warning, and the point of naming the price first is that a reader can stop before paying it.
//
// A baseline the store already owned still owes the notice — an earlier pass registered the row, and the pass
// this call is about to run rescans both trees just the same.
//
// `Skipped' is the state where the flag was asked and nothing could be registered, because this pass created no
// store to register it in. It carries a reason and no cost: a run that did half of what was asked and said
// nothing is how a flag starts lying, so the state arrives on the report rather than as silence.
public sealed record WorktreeBaselineOutcome(
    WorktreeBaselineState State,
    string RepoName,
    string? BaselineRootPath,
    string LaneRootPath,
    string? CostNotice,
    string? Reason)
{
    public bool PaysCost => State is WorktreeBaselineState.Registered or WorktreeBaselineState.AlreadyRegistered;

    public static WorktreeBaselineOutcome AlreadyThere(
        string laneRoot, string baselineRoot, string costNotice) =>
        new(WorktreeBaselineState.AlreadyRegistered, WorktreeBaseline.RepoName, baselineRoot, laneRoot,
            costNotice,
            $"repo '{WorktreeBaseline.RepoName}' in this store already names '{baselineRoot}', so no row was " +
            "registered again and this call owes no pass — the proof on the store says what it measured");

    public static WorktreeBaselineOutcome Registered(
        string laneRoot, string baselineRoot, string costNotice) =>
        new(WorktreeBaselineState.Registered, WorktreeBaseline.RepoName, baselineRoot, laneRoot, costNotice, null);

    public static WorktreeBaselineOutcome Skipped(string laneRoot, string reason) =>
        new(WorktreeBaselineState.Skipped, WorktreeBaseline.RepoName, null, laneRoot, null, reason);
}
