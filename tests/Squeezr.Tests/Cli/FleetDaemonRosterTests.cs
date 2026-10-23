using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Cli;

public sealed class FleetDaemonRosterTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _storeDir = TestDirs.NewTempDir();

    [Fact]
    public void TwoLiveBeats_ListsBoth_AndMarksExactlyTheClaimOwner()
    {
        WriteBeat(new DaemonHeartbeat(4242, DaemonHeartbeat.ServeWatchKind, Now, Now, "box"));
        WriteBeat(new DaemonHeartbeat(4343, DaemonHeartbeat.ServeWatchKind, Now, Now, "box"));
        WriteClaim(new WatchOwnerMarker(4242, "box", DaemonHeartbeat.ServeWatchKind, Now, Now));

        var roster = Probe(pid => true);

        roster.Select(daemon => daemon.Pid).ShouldBe([4242, 4343]);
        roster.Count(daemon => daemon.Owner).ShouldBe(1);
        roster.Single(daemon => daemon.Owner).Pid.ShouldBe(4242);
        roster.ShouldAllBe(daemon => daemon.IsAlive);
    }

    [Fact]
    public void KindHeartbeatWithoutAnyClaim_NamesNoOwner_RatherThanGuessingOne()
    {
        File.WriteAllText(
            DaemonHeartbeat.PathFor(_storeDir, DaemonHeartbeat.DefaultKind),
            JsonSerializer.Serialize(
                new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now, Now), SqueezrJson.FileOptions));

        Probe(pid => true).ShouldHaveSingleItem().Owner.ShouldBeFalse();
    }

    [Fact]
    public void GonePidWithAStaleBeat_IsListedAsDead_NeverAsAlive()
    {
        WriteBeat(new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now.AddHours(-2), Now.AddMinutes(-5), "box"));

        var row = Probe(pid => false).ShouldHaveSingleItem();
        row.Status.ShouldBe(FleetDaemonState.DeadStatus);
        row.IsAlive.ShouldBeFalse();
        row.UptimeSeconds.ShouldBeNull();
        row.Pid.ShouldBe(4242);
    }

    [Fact]
    public void OwnerWritesBothShapes_AnswersTheRosterOnce_AndKeepsItsClaim()
    {
        WriteBeat(new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now, Now, "box"));
        File.WriteAllText(
            DaemonHeartbeat.PathFor(_storeDir, DaemonHeartbeat.DefaultKind),
            JsonSerializer.Serialize(
                new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now, Now, "box"), SqueezrJson.FileOptions));
        WriteClaim(new WatchOwnerMarker(4242, "box", DaemonHeartbeat.DefaultKind, Now, Now));

        var roster = Probe(pid => true);

        var row = roster.ShouldHaveSingleItem("the owner answers once from its beat and once from its mirror");
        row.Owner.ShouldBeTrue();
    }

    [Fact]
    public void ClaimHeldByAnotherHost_NamesNoOwnerHere_ItsPidIsNotOurs()
    {
        WriteBeat(new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now, Now, "box"));
        WriteClaim(new WatchOwnerMarker(4242, "elsewhere", DaemonHeartbeat.DefaultKind, Now, Now));

        Probe(pid => true).ShouldHaveSingleItem().Owner.ShouldBeFalse();
    }

    [Fact]
    public void BeatNamingAnotherHost_KeepsItsRow_AndItsHost()
    {
        WriteBeat(new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now, Now, "elsewhere"));

        var row = Probe(pid => true).ShouldHaveSingleItem();
        row.Host.ShouldBe("elsewhere");
        row.Pid.ShouldBe(4242);
    }

    [Fact]
    public void StrangerInBeatsDir_IsNotARow()
    {
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(_storeDir));
        File.WriteAllText(Path.Combine(DaemonHeartbeat.BeatsDirFor(_storeDir), "watch.4242.beat.tmp"), "{}");
        File.WriteAllText(Path.Combine(DaemonHeartbeat.BeatsDirFor(_storeDir), "notes.txt"), "hello");
        WriteBeat(new DaemonHeartbeat(4343, DaemonHeartbeat.DefaultKind, Now, Now, "box"));

        Probe(pid => true).ShouldHaveSingleItem().Pid.ShouldBe(4343);
    }

    // The roster groups by kind (ordinal) and leads each group with the owner, so a kind's cell reads
    // owner-first and a rerun of the same store renders identically.
    [Fact]
    public void RosterGroupsByKind_LeadingEachGroupWithTheOwner_SoTheTableIsStable()
    {
        WriteBeat(new DaemonHeartbeat(4343, DaemonHeartbeat.DefaultKind, Now, Now, "box"));
        WriteBeat(new DaemonHeartbeat(4242, DaemonHeartbeat.DefaultKind, Now, Now, "box"));
        WriteBeat(new DaemonHeartbeat(4444, DaemonHeartbeat.ServeWatchKind, Now, Now, "box"));
        WriteClaim(new WatchOwnerMarker(4242, "box", DaemonHeartbeat.DefaultKind, Now, Now));

        var roster = Probe(pid => true);

        roster.Select(daemon => (daemon.Kind, daemon.Pid, daemon.Owner)).ShouldBe([
            (DaemonHeartbeat.ServeWatchKind, 4444, false),
            (DaemonHeartbeat.DefaultKind, 4242, true),
            (DaemonHeartbeat.DefaultKind, 4343, false),
        ]);
    }

    private IReadOnlyList<FleetDaemonProcess> Probe(Func<int, bool> isProcessAlive) =>
        FleetListing.ProbeDaemons(_storeDir, Now, isProcessAlive);

    private void WriteBeat(DaemonHeartbeat beat)
    {
        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(_storeDir));
        File.WriteAllText(
            DaemonHeartbeat.BeatPathFor(_storeDir, beat.Kind, beat.Pid),
            JsonSerializer.Serialize(beat, SqueezrJson.FileOptions));
    }

    private void WriteClaim(WatchOwnerMarker marker) =>
        File.WriteAllText(
            WatchOwnerMarker.PathFor(_storeDir), JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));

    public void Dispose()
    {
        Directory.Delete(_storeDir, recursive: true);
    }
}
