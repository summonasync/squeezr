using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Cli;

public sealed class FleetStopTargetingTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly int GonePid = int.MaxValue;
    private readonly string _storeDir = TestDirs.NewTempDir();

    [Fact]
    public void ClaimOwnerIsTheTarget_TheLyingMirrorIsNot()
    {
        WriteClaim(new WatchOwnerMarker(Environment.ProcessId, Environment.MachineName, DaemonHeartbeat.DefaultKind, Now, Now));
        WriteMirror(DaemonHeartbeat.DefaultKind, GonePid, Now, Now);

        var (targets, observers, reason) = FleetStopCommand.ResolveKillTargets(_storeDir, Now);

        targets.ShouldHaveSingleItem().Heartbeat.Pid.ShouldBe(Environment.ProcessId);
        targets[0].Kind.ShouldBe(DaemonHeartbeat.DefaultKind);
        observers.ShouldBeEmpty("a mirror naming a dead process is neither the owner nor a live observer");
        reason.ShouldBeNull();
    }

    [Fact]
    public void GoneOwnerPid_StopsNothing_AndSaysWhoIsGone()
    {
        WriteClaim(new WatchOwnerMarker(GonePid, Environment.MachineName, DaemonHeartbeat.DefaultKind, Now, Now));

        var (targets, observers, reason) = FleetStopCommand.ResolveKillTargets(_storeDir, Now);

        targets.ShouldBeEmpty();
        observers.ShouldBeEmpty();
        reason.ShouldNotBeNull().ShouldContain("gone");
    }

    [Fact]
    public void ForeignHostClaim_NoLocalTarget_ReasonNamesTheHost()
    {
        WriteClaim(new WatchOwnerMarker(Environment.ProcessId, "elsewhere", DaemonHeartbeat.ServeWatchKind, Now, Now));

        var (targets, _, reason) = FleetStopCommand.ResolveKillTargets(_storeDir, Now);

        targets.ShouldBeEmpty("a foreign pid lives in a namespace this machine may not signal");
        reason.ShouldNotBeNull().ShouldContain("elsewhere");
    }

    [Fact]
    public void NoClaim_AtLivelyMirrorStillStopsAsBefore_ThePreClaimShape()
    {
        WriteMirror(DaemonHeartbeat.DefaultKind, Environment.ProcessId, Now, Now);

        var (targets, observers, reason) = FleetStopCommand.ResolveKillTargets(_storeDir, Now);

        targets.ShouldHaveSingleItem().Heartbeat.Pid.ShouldBe(Environment.ProcessId);
        observers.ShouldBeEmpty();
        reason.ShouldBeNull();
    }

    [Fact]
    public void NoClaim_DeadMirror_StopsNothing()
    {
        WriteMirror(DaemonHeartbeat.ServeWatchKind, GonePid, Now, Now);

        var (targets, observers, reason) = FleetStopCommand.ResolveKillTargets(_storeDir, Now);

        targets.ShouldBeEmpty();
        observers.ShouldBeEmpty();
        reason.ShouldBeNull();
    }

    [LinuxFact]
    public void ClaimOwnerTargeted_LiveBeatOfAnotherProcess_IsCounted_NotTargeted()
    {
        var observerPid = Environment.ProcessId;
        WriteClaim(new WatchOwnerMarker(1, Environment.MachineName, DaemonHeartbeat.ServeWatchKind, Now, Now));
        WriteBeat(observerPid, DaemonHeartbeat.DefaultKind, Now, Now);

        var (targets, observers, _) = FleetStopCommand.ResolveKillTargets(_storeDir, Now);

        targets.ShouldHaveSingleItem().Heartbeat.Pid.ShouldBe(1, "pid 1 is the only guaranteed-live process besides this one");
        targets[0].Kind.ShouldBe(DaemonHeartbeat.ServeWatchKind);
        observers.ShouldHaveSingleItem().Pid.ShouldBe(observerPid);
    }

    private void WriteClaim(WatchOwnerMarker marker) =>
        File.WriteAllText(WatchOwnerMarker.PathFor(_storeDir), JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));

    private void WriteMirror(string kind, int pid, DateTime startedAt, DateTime lastBeatAt) =>
        File.WriteAllText(
            DaemonHeartbeat.PathFor(_storeDir, kind),
            JsonSerializer.Serialize(new DaemonHeartbeat(pid, kind, startedAt, lastBeatAt), SqueezrJson.FileOptions));

    private void WriteBeat(int pid, string kind, DateTime startedAt, DateTime lastBeatAt)
    {
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(_storeDir));
        File.WriteAllText(
            DaemonHeartbeat.BeatPathFor(_storeDir, kind, pid),
            JsonSerializer.Serialize(
                new DaemonHeartbeat(pid, kind, startedAt, lastBeatAt, Environment.MachineName),
                SqueezrJson.FileOptions));
    }

    public void Dispose() => Directory.Delete(_storeDir, recursive: true);
}
