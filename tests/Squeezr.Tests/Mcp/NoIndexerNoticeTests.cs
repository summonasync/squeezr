using Shouldly;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Tests.Mcp;

public sealed class NoIndexerNoticeTests
{
    private static readonly WatchStoreHealth Unowned = new(WatchIndexerState.None, null, null, null, 0);
    private static readonly WatchStoreHealth DeadOwner = new(WatchIndexerState.Gone, 1001, "watch", "h", 0);
    private static readonly WatchStoreHealth WedgedOwner = new(WatchIndexerState.Wedged, 4242, "watch", "h", 0);

    [Theory]
    [InlineData(WatchIndexerState.None)]
    [InlineData(WatchIndexerState.Gone)]
    [InlineData(WatchIndexerState.Wedged)]
    public void ForAgent_WithoutSupervision_KeepsLegacyTextByteForByte(WatchIndexerState state)
    {
        var health = Health(state);
        var unsupervised = NoIndexerNotice.ForAgent(health);
        var explicitFalse = NoIndexerNotice.ForAgent(health, supervised: false);

        unsupervised.ShouldBe(NoIndexerNotice.ForAgent(health));
        explicitFalse.ShouldBe(unsupervised);
        unsupervised.ShouldBe(ExpectedLegacyAgentLine(state));
    }

    [Theory]
    [InlineData(WatchIndexerState.None)]
    [InlineData(WatchIndexerState.Gone)]
    [InlineData(WatchIndexerState.Wedged)]
    public void StartupLine_WithoutSupervision_KeepsLegacyTextByteForByte(WatchIndexerState state)
    {
        var health = Health(state);
        var unsupervised = NoIndexerNotice.StartupLine(health);
        var explicitFalse = NoIndexerNotice.StartupLine(health, supervised: false);

        unsupervised.ShouldBe(NoIndexerNotice.StartupLine(health));
        explicitFalse.ShouldBe(unsupervised);
        unsupervised.ShouldBe(ExpectedLegacyStartupLine(state));
    }

    [Theory]
    [InlineData(WatchIndexerState.None)]
    [InlineData(WatchIndexerState.Gone)]
    public void ForAgent_WhenWatchdSupervises_SaysSupervisorBringsTheWatcherUp(WatchIndexerState state)
    {
        var notice = NoIndexerNotice.ForAgent(Health(state), supervised: true);

        notice.ShouldContain("under watchd supervision");
        notice.ShouldContain("supervisor will bring the watcher up");
        notice.ShouldContain("'squeezr watch' is not needed");
        notice.ShouldNotContain("run 'squeezr watch'");
    }

    [Fact]
    public void ForAgent_WhenWedgedUnderSupervision_NamesPidAndFleetRestart()
    {
        var notice = NoIndexerNotice.ForAgent(WedgedOwner, supervised: true);

        notice.ShouldContain("wedged under watchd supervision");
        notice.ShouldContain("pid 4242");
        notice.ShouldContain("'squeezr fleet restart'");
        notice.ShouldNotContain("then 'squeezr watch'");
    }

    [Fact]
    public void StartupLine_WhenWatchdSupervises_SaysNoManualWatchIsNeeded()
    {
        var line = NoIndexerNotice.StartupLine(Unowned, supervised: true);

        line.ShouldStartWith("warning: no background indexer on this store:");
        line.ShouldContain("watchd supervises it and will start the watcher");
        line.ShouldContain("'squeezr watch' is not needed");
    }

    [Fact]
    public void StartupLine_WhenWedgedUnderSupervision_NamesPidAndFleetRestart()
    {
        var line = NoIndexerNotice.StartupLine(WedgedOwner, supervised: true);

        line.ShouldStartWith("warning: watch claim held by wedged pid 4242");
        line.ShouldContain("store under watchd supervision");
        line.ShouldContain("'squeezr fleet restart'");
    }

    [Fact]
    public void ForAgent_WhenSupervisedButIndexerRuns_FallsBackToLegacyDefault()
    {
        var alive = new WatchStoreHealth(WatchIndexerState.Alive, 77, "watch", "h", 0);

        NoIndexerNotice.ForAgent(alive, supervised: true).ShouldBe(NoIndexerNotice.ForAgent(alive));
        NoIndexerNotice.StartupLine(alive, supervised: true).ShouldBe(NoIndexerNotice.StartupLine(alive));
    }

    [Fact]
    public void ForAgent_WhenTheStateFileIsUnreadable_SaysNoWatcherIsComing_AndNamesTheRepair()
    {
        var notice = NoIndexerNotice.ForAgent(Unowned, NoIndexerNotice.Supervision.StateUnreadable);

        notice.ShouldContain("desired state is unknown");
        notice.ShouldContain("state file cannot be read");
        notice.ShouldContain("no watcher is coming until the file is repaired");
        notice.ShouldNotContain("watchd supervision and the supervisor will bring the watcher up");
    }

    [Fact]
    public void StartupLine_WhenTheStateFileIsUnreadable_SaysTheSameOnStderr()
    {
        var line = NoIndexerNotice.StartupLine(Unowned, NoIndexerNotice.Supervision.StateUnreadable);

        line.ShouldStartWith("warning: no background indexer on this store:");
        line.ShouldContain("state file cannot be read");
        line.ShouldContain("no watcher is coming until the file is repaired");
        line.ShouldNotContain("will start the watcher");
    }

    [Fact]
    public void ForAgent_WhenWedgedAndTheStateFileIsUnreadable_DoesNotPromiseASupervisor()
    {
        var notice = NoIndexerNotice.ForAgent(WedgedOwner, NoIndexerNotice.Supervision.StateUnreadable);

        notice.ShouldContain("wedged");
        notice.ShouldContain("pid 4242");
        notice.ShouldContain("cannot supervise anything until its state file parses again");
        notice.ShouldNotContain("the supervisor may be blocked from restarting it");
    }

    [Fact]
    public void StartupLine_WhenWedgedAndTheStateFileIsUnreadable_StillAsksForNoActionItCannotTake()
    {
        var line = NoIndexerNotice.StartupLine(WedgedOwner, NoIndexerNotice.Supervision.StateUnreadable);

        line.ShouldStartWith("warning: watch claim held by wedged pid 4242");
        line.ShouldContain("supervision is paused while its state file cannot be read");
        line.ShouldContain("'squeezr fleet restart'");
    }

    // A store whose watcher is alive gets no notice at all, unreadable state or not: nothing is missing.
    [Fact]
    public void ForAgent_WhenIndexerRuns_UnreadableStateDoesNotChangeTheVerdict()
    {
        var alive = new WatchStoreHealth(WatchIndexerState.Alive, 77, "watch", "h", 0);

        NoIndexerNotice.ForAgent(alive, NoIndexerNotice.Supervision.StateUnreadable)
            .ShouldBe(NoIndexerNotice.ForAgent(alive));
        NoIndexerNotice.StartupLine(alive, NoIndexerNotice.Supervision.StateUnreadable)
            .ShouldBe(NoIndexerNotice.StartupLine(alive));
    }

    [Theory]
    [InlineData(WatchIndexerState.None)]
    [InlineData(WatchIndexerState.Gone)]
    [InlineData(WatchIndexerState.Wedged)]
    public void SupervisionReadings_DifferFromEachOtherOnEveryState(WatchIndexerState state)
    {
        var health = Health(state);

        NoIndexerNotice.ForAgent(health, NoIndexerNotice.Supervision.None)
            .ShouldNotBe(NoIndexerNotice.ForAgent(health, NoIndexerNotice.Supervision.WillBringItUp));
        NoIndexerNotice.ForAgent(health, NoIndexerNotice.Supervision.StateUnreadable)
            .ShouldNotBe(NoIndexerNotice.ForAgent(health, NoIndexerNotice.Supervision.WillBringItUp));
        NoIndexerNotice.ForAgent(health, NoIndexerNotice.Supervision.StateUnreadable)
            .ShouldNotBe(NoIndexerNotice.ForAgent(health, NoIndexerNotice.Supervision.None));
    }

    private static WatchStoreHealth Health(WatchIndexerState state) => state switch
    {
        WatchIndexerState.Gone => DeadOwner,
        WatchIndexerState.Wedged => WedgedOwner,
        _ => Unowned,
    };

    private static string ExpectedLegacyAgentLine(WatchIndexerState state) => state switch
    {
        WatchIndexerState.Wedged =>
            "no working background indexer on this store: pid 4242 holds the watch claim with a stale " +
            "beat and runs no passes; this session answers queries and refreshes only what a query touches, so the " +
            "index misses disk edits until the watcher restarts ('squeezr fleet stop', then 'squeezr watch')",
        WatchIndexerState.Gone =>
            "no background indexer is running on this store: the old watch claim named pid 1001, " +
            "which is gone; this session answers queries and refreshes only what a query touches, so the index " +
            "misses disk edits until a watcher starts ('squeezr watch' in this workspace)",
        _ =>
            "no background indexer is running on this store: this session answers queries and refreshes only what " +
            "a query touches, so the index misses disk edits until a watcher starts ('squeezr watch' in this " +
            "workspace, or 'squeezr mcp doctor' for the exact command)",
    };

    private static string ExpectedLegacyStartupLine(WatchIndexerState state) => state switch
    {
        WatchIndexerState.Wedged =>
            "warning: watch claim held by wedged pid 4242: no indexing passes run; " +
            "queries refresh only what they touch",
        WatchIndexerState.Gone =>
            "warning: no background indexer on this store (the old claim named pid 1001, now gone): " +
            "queries refresh only what they touch; run 'squeezr watch' to chase edits",
        _ =>
            "warning: no background indexer on this store: queries refresh only what they touch; " +
            "run 'squeezr watch' to chase edits",
    };
}
