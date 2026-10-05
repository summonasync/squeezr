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

    public static RepoGuardReport? From(int withheld, int withheldByBuiltIns, int dropped)
    {
        if (withheld < 0 || withheldByBuiltIns < 0 || withheldByBuiltIns > withheld || dropped < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(withheldByBuiltIns));
        }

        return withheld == 0 && dropped == 0 ? null : new RepoGuardReport(withheld, withheldByBuiltIns, dropped);
    }

    public string Warning() =>
        $"secret guard: {Withheld} file(s) withheld from indexing ({Origin()}), {Dropped} protected row(s) " +
        $"removed from the store — paths are not named";

    private string Origin() => SecretGuard.DescribeOrigins(Withheld, WithheldByBuiltIns);
}
