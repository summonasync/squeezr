using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Tests.Cli;

public sealed class FleetSupervisionPostureTests
{
    private static readonly string LocalHost = Environment.MachineName;

    [Fact]
    public void WedgedClaim_WinsOverDesireAndWatchd()
    {
        var wedged = Health(WatchIndexerState.Wedged, 4242, DaemonHeartbeat.DefaultKind, LocalHost);

        FleetSupervisionPosture.Resolve(WatchDesire.Running, wedged, watchdRunning: true)
            .ShouldBe(FleetSupervisionPosture.Wedged);
        FleetSupervisionPosture.Resolve(WatchDesire.Stopped, wedged, watchdRunning: false)
            .ShouldBe(FleetSupervisionPosture.Wedged);
    }

    [Fact]
    public void LiveLocalWatch_UnderLiveWatchd_IsSupervised()
    {
        var alive = Health(WatchIndexerState.Alive, 4242, DaemonHeartbeat.DefaultKind, LocalHost);

        FleetSupervisionPosture.Resolve(WatchDesire.Running, alive, watchdRunning: true)
            .ShouldBe(FleetSupervisionPosture.Supervised);
    }

    [Fact]
    public void ServeWatchOwner_IsManagedElsewhere_EvenUnderLiveWatchd()
    {
        var serveWatch = Health(WatchIndexerState.Alive, 4242, DaemonHeartbeat.ServeWatchKind, LocalHost);

        FleetSupervisionPosture.Resolve(WatchDesire.Running, serveWatch, watchdRunning: true)
            .ShouldBe(FleetSupervisionPosture.ManagedElsewhere);
    }

    [Fact]
    public void LiveOwner_WithoutWatchd_IsManagedElsewhere()
    {
        var alive = Health(WatchIndexerState.Alive, 4242, DaemonHeartbeat.DefaultKind, LocalHost);

        FleetSupervisionPosture.Resolve(WatchDesire.Running, alive, watchdRunning: false)
            .ShouldBe(FleetSupervisionPosture.ManagedElsewhere);
    }

    [Fact]
    public void ForeignOwner_IsManagedElsewhere()
    {
        var foreign = Health(WatchIndexerState.Foreign, 4242, DaemonHeartbeat.DefaultKind, "elsewhere");

        FleetSupervisionPosture.Resolve(WatchDesire.Running, foreign, watchdRunning: true)
            .ShouldBe(FleetSupervisionPosture.ManagedElsewhere);
    }

    [Fact]
    public void DesiredStopped_LiveOwnerOrNot_ReadsStopped()
    {
        var alive = Health(WatchIndexerState.Alive, 4242, DaemonHeartbeat.DefaultKind, LocalHost);

        FleetSupervisionPosture.Resolve(WatchDesire.Stopped, alive, watchdRunning: true)
            .ShouldBe(FleetSupervisionPosture.Stopped);
        FleetSupervisionPosture.Resolve(WatchDesire.Stopped, Health(WatchIndexerState.None), watchdRunning: false)
            .ShouldBe(FleetSupervisionPosture.Stopped);
    }

    [Fact]
    public void NothingAlive_UnderLiveWatchd_IsStarting_UntilItComesUp()
    {
        FleetSupervisionPosture.Resolve(WatchDesire.Running, Health(WatchIndexerState.None), watchdRunning: true)
            .ShouldBe(FleetSupervisionPosture.Starting);
        FleetSupervisionPosture.Resolve(WatchDesire.Running, Health(WatchIndexerState.Gone), watchdRunning: true)
            .ShouldBe(FleetSupervisionPosture.Starting);
    }

    [Fact]
    public void NothingAlive_WithoutWatchd_IsStopped()
    {
        FleetSupervisionPosture.Resolve(WatchDesire.Running, Health(WatchIndexerState.None), watchdRunning: false)
            .ShouldBe(FleetSupervisionPosture.Stopped);
    }

    private static WatchStoreHealth Health(
        WatchIndexerState state,
        int? pid = null,
        string? kind = null,
        string? host = null) =>
        new(state, pid, kind, host, 0);
}
