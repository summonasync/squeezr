using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// What the secret guard reported on its way out of a pass, in one shape so no surface invents its own wording.
// It carries no paths: a report about withheld secrets that named one would be the hole this feature exists
// to close. The read side stays separate on purpose — a withheld result says nothing about ingestion.
public sealed class RepoGuardReport
{
    private RepoGuardReport(int withheld, int withheldByBuiltIns, int dropped)
    {
        Withheld = withheld;
        WithheldByBuiltIns = withheldByBuiltIns;
        Dropped = dropped;
    }

    public int Withheld { get; }

    public int WithheldByBuiltIns { get; }

    public int WithheldByConfig => Withheld - WithheldByBuiltIns;

    public int Dropped { get; }

    public static RepoGuardReport? FromTally(IReadOnlyList<RepoStatus> repos) =>
        From(repos.Sum(r => r.GuardWithheld), repos.Sum(r => r.GuardWithheldBuiltIns), repos.Sum(r => r.GuardDropped));

    public static RepoGuardReport? FromRepo(RepoStatus repo) =>
        From(repo.GuardWithheld, repo.GuardWithheldBuiltIns, repo.GuardDropped);

    // A corrupt ledger clamps, it does not throw: this shape only reports, and a stored counter that adds up
    // wrong must not cost the caller the whole answer — `squeezr status` has no catch and index_status would
    // degrade to a tool error. The overshoot is the interesting half, so it is capped at `withheld`.
    public static RepoGuardReport? From(int withheld, int withheldByBuiltIns, int dropped)
    {
        withheld = Math.Max(0, withheld);
        var builtIns = Math.Clamp(withheldByBuiltIns, 0, withheld);
        dropped = Math.Max(0, dropped);

        return withheld == 0 && dropped == 0 ? null : new RepoGuardReport(withheld, builtIns, dropped);
    }

    public string Warning() =>
        $"secret guard: {Withheld} file(s) withheld from indexing ({Origin()}), {Dropped} protected row(s) " +
        $"removed from the store — paths are not named";

    private string Origin() => SecretGuard.DescribeOrigins(Withheld, WithheldByBuiltIns);
}
