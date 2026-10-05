using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

// The report shape only ever reads stored counters, so its contract is: render what the ledger says, and when
// the ledger contradicts itself, clamp it into a renderable shape instead of throwing. A corrupt counter must
// not cost the caller the whole answer — `squeezr status` has no catch, and index_status would come back as a
// tool error over one bad number. These are the shapes a hand-edited or half-migrated ledger can hold.
public class RepoGuardReportTests
{
    [Fact]
    public void From_NegativeWithheldWithADrop_RendersClampedCountersInsteadOfThrowing()
    {
        var report = RepoGuardReport.From(-1, 5, 2);

        report.ShouldNotBeNull();
        report.Withheld.ShouldBe(0);
        report.WithheldByBuiltIns.ShouldBe(0);
        report.WithheldByConfig.ShouldBe(0);
        report.Dropped.ShouldBe(2);
        report.Warning().ShouldContain("0 file(s) withheld from indexing");
        report.Warning().ShouldContain("2 protected row(s) removed from the store");
    }

    [Fact]
    public void From_CountersThatAllClampToZero_ReportNothing()
    {
        RepoGuardReport.From(0, 0, 0).ShouldBeNull();
        RepoGuardReport.From(-1, 5, 0).ShouldBeNull();
        RepoGuardReport.From(0, 0, -7).ShouldBeNull();
    }

    [Fact]
    public void From_NegativeDropped_ClampsToZeroAndStillReportsTheWithholding()
    {
        var report = RepoGuardReport.From(3, 1, -2);

        report.ShouldNotBeNull();
        report.Withheld.ShouldBe(3);
        report.WithheldByBuiltIns.ShouldBe(1);
        report.WithheldByConfig.ShouldBe(2);
        report.Dropped.ShouldBe(0);
    }

    [Fact]
    public void From_BuiltInsOvershootingWithheld_AreCappedSoTheConfiguredHalfStaysNonNegative()
    {
        var report = RepoGuardReport.From(2, 5, 0);

        report.ShouldNotBeNull();
        report.Withheld.ShouldBe(2);
        report.WithheldByBuiltIns.ShouldBe(2);
        report.WithheldByConfig.ShouldBe(0);
    }

    [Fact]
    public void FromRepo_ALedgerThatOverstatesItsBuiltIns_RendersTheBuiltInOrigin()
    {
        var repo = new RepoStatus("demo", "/repo", 1, 0, 1, DateTime.UtcNow,
            GuardWithheld: 2, GuardWithheldBuiltIns: 9, GuardDropped: -1);

        var report = RepoGuardReport.FromRepo(repo);

        report.ShouldNotBeNull();
        report.WithheldByBuiltIns.ShouldBe(2);
        report.Dropped.ShouldBe(0);
        report.Warning().ShouldContain("2 file(s) withheld from indexing (built-in patterns)");
    }

    [Fact]
    public void FromTally_ASummedLedgerThatOnlyHoldsCorruption_StillRendersTheRealDrop()
    {
        var repos = new[]
        {
            new RepoStatus("demo", "/repo", 1, 0, 1, DateTime.UtcNow,
                GuardWithheld: -2, GuardWithheldBuiltIns: 3, GuardDropped: 1),
        };

        var report = RepoGuardReport.FromTally(repos);

        report.ShouldNotBeNull();
        report.Withheld.ShouldBe(0);
        report.WithheldByBuiltIns.ShouldBe(0);
        report.Dropped.ShouldBe(1);
    }
}
