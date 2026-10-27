using System.Collections.Concurrent;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Indexing.Watch;

// What a crashed reaper leaves behind: the reservation half of the compare-and-rename survives as a
// watch.owner.*.reap remnant, and the next successful claim sweeps it without touching live claims —
// naming the swept dead owner as displaced when the reservation is all that remains of the adoption
// story. The live-race gap shape lands here too: whoever reaped the dead marker but lost the create
// leaves the adoption event on the store for the session that took the gap.
public sealed class WatchOwnerClaimRemnantTests : IDisposable
{
    private const int DeadPid = 4100;
    private const int OtherPid = 4200;
    private const int GapWinnerPid = 5300;

    private static readonly DateTime T0 = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _storeDir = TestDirs.NewTempDir();
    private readonly ConcurrentQueue<string> _warnings = [];
    private readonly HashSet<int> _alivePids = [];

    private DateTime _now = T0;

    [Fact]
    public void TryAcquire_AfterCrashBetweenReapRenameAndDelete_SweepsTheRemnantAndNamesTheDisplacedOwner()
    {
        var dead = MarkerOwnedBy(DeadPid, beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10));
        WriteReservation(dead);

        var outcome = CreateClaim(pid: 5200).TryAcquire();

        outcome.IsOwner.ShouldBeTrue();
        Reservations().ShouldBeEmpty("the crashed reaper's reservation must not outlive the next claim");
        var adopted = WatchAdoptionEventFile.TryRead(_storeDir).ShouldNotBeNull();
        adopted.AdopterPid.ShouldBe(5200);
        adopted.DisplacedPid.ShouldBe(DeadPid);
        adopted.DisplacedLastBeatAt.ShouldBe(dead.LastBeatAt);
        _warnings.ShouldBeEmpty();
    }

    [Fact]
    public void TryAcquire_LiveOwnerHoldsTheStore_LeavesBothTheClaimAndTheRemnantAlone()
    {
        _alivePids.Add(OtherPid);
        var live = MarkerOwnedBy(OtherPid, beatAge: TimeSpan.FromSeconds(2));
        WriteMarker(live);
        WriteReservation(MarkerOwnedBy(
            DeadPid, beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10)));

        var outcome = CreateClaim(pid: 5200).TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.OwnerHold);
        outcome.IsOwner.ShouldBeFalse();
        ReadMarkerFile().ShouldBe(live);
        Reservations().ShouldHaveSingleItem("a held store is not a successful cycle, so nothing sweeps yet");
        WatchAdoptionEventFile.TryRead(_storeDir).ShouldBeNull();
    }

    [Fact]
    public void TryAcquire_ReapingDeadOwner_SweepsOlderRemnantsWithoutRewritingTheirStory()
    {
        WriteReservation(MarkerOwnedBy(DeadPid - 1, beatAge: TimeSpan.FromHours(1)));
        var dead = MarkerOwnedBy(DeadPid, beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10));
        WriteMarker(dead);

        var outcome = CreateClaim(pid: 5200).TryAcquire();

        outcome.IsOwner.ShouldBeTrue();
        outcome.TookOverFrom!.Pid.ShouldBe(DeadPid);
        Reservations().ShouldBeEmpty();
        WatchAdoptionEventFile.TryRead(_storeDir)!.DisplacedPid.ShouldBe(DeadPid);
    }

    [Fact]
    public void TryAcquire_ReapedOwnerButLostTheCreateRace_LeavesTheAdoptionStoryForTheGapWinner()
    {
        var dead = MarkerOwnedBy(DeadPid, beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10));
        WriteMarker(dead);
        var gapWinner = MarkerOwnedBy(GapWinnerPid, beatAge: TimeSpan.FromSeconds(1));
        _alivePids.Add(GapWinnerPid);

        var outcome = CreateClaim(pid: 5200, markerCreate: _ =>
        {
            WatchOwnerMarkerFile.TryCreate(WatchOwnerMarker.PathFor(_storeDir), gapWinner);
            return false;
        }).TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.RaceLost);
        outcome.IsOwner.ShouldBeFalse();
        outcome.Owner!.Pid.ShouldBe(GapWinnerPid);
        ReadMarkerFile().ShouldBe(gapWinner);
        var adopted = WatchAdoptionEventFile.TryRead(_storeDir).ShouldNotBeNull();
        adopted.AdopterPid.ShouldBe(GapWinnerPid);
        adopted.AdopterHost.ShouldBe(Environment.MachineName);
        adopted.DisplacedPid.ShouldBe(DeadPid);
        adopted.DisplacedLastBeatAt.ShouldBe(dead.LastBeatAt);
        _warnings.ShouldBeEmpty();
    }

    [Fact]
    public void TryAcquire_UnreadableTakeoverLosingTheCreateRace_RecordsNoEventWithoutADisplacedOwner()
    {
        WriteGarbageMarker(age: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(15));
        var gapWinner = MarkerOwnedBy(GapWinnerPid, beatAge: TimeSpan.FromSeconds(1));
        _alivePids.Add(GapWinnerPid);

        var outcome = CreateClaimWithRealClock(pid: 5200, markerReap: _ =>
        {
            WatchOwnerMarkerFile.TryCreate(WatchOwnerMarker.PathFor(_storeDir), gapWinner);
            return true;
        }).TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.RaceLost);
        WatchAdoptionEventFile.TryRead(_storeDir).ShouldBeNull();
    }

    [Fact]
    public void TryAcquire_ReservationStillFreshFromALiveReaper_IsLeftForItsOwnCycleToFinish()
    {
        WriteReservation(
            MarkerOwnedBy(DeadPid, beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10)),
            age: TimeSpan.Zero);

        var outcome = CreateClaim(pid: 5200).TryAcquire();

        outcome.IsOwner.ShouldBeTrue();
        Reservations().ShouldHaveSingleItem("a reservation this fresh may belong to a live peer's reap in flight");
        WatchAdoptionEventFile.TryRead(_storeDir).ShouldBeNull();
        _warnings.ShouldBeEmpty();
    }

    [Fact]
    public void TryAcquire_RemnantsFromRepeatedCrashes_RecordsTheMostRecentlyBeatenOwner()
    {
        WriteReservation(MarkerOwnedBy(DeadPid - 1, beatAge: TimeSpan.FromHours(2)));
        var recent = MarkerOwnedBy(DeadPid, beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10));
        WriteReservation(recent);

        var outcome = CreateClaim(pid: 5200).TryAcquire();

        outcome.IsOwner.ShouldBeTrue();
        Reservations().ShouldBeEmpty();
        var adopted = WatchAdoptionEventFile.TryRead(_storeDir).ShouldNotBeNull();
        adopted.DisplacedPid.ShouldBe(DeadPid);
        adopted.DisplacedLastBeatAt.ShouldBe(recent.LastBeatAt);
    }

    [Fact]
    public void TryAcquire_RemnantNamesThisProcessAsDisplaced_SkipsSelfAndNamesAnOlderOwner()
    {
        WriteReservation(MarkerOwnedBy(5200, beatAge: TimeSpan.FromSeconds(5)));
        var older = MarkerOwnedBy(DeadPid, beatAge: TimeSpan.FromHours(1));
        WriteReservation(older);

        var outcome = CreateClaim(pid: 5200).TryAcquire();

        outcome.IsOwner.ShouldBeTrue();
        Reservations().ShouldBeEmpty();
        WatchAdoptionEventFile.TryRead(_storeDir)!.DisplacedPid.ShouldBe(
            DeadPid, "a reused pid must not end up adopting itself in the fleet view");
    }

    private WatchOwnerClaim CreateClaim(
        int? pid = null,
        Func<WatchOwnerMarker, bool>? markerCreate = null,
        Func<WatchOwnerMarker?, bool>? markerReap = null) =>
        new(
            _storeDir, DaemonHeartbeat.DefaultKind, () => _now, IsPidMarkedAlive, _warnings.Enqueue, pid,
            markerCreate, markerReap);

    private WatchOwnerClaim CreateClaimWithRealClock(
        int? pid = null,
        Func<WatchOwnerMarker?, bool>? markerReap = null) =>
        new(_storeDir, DaemonHeartbeat.DefaultKind, null, IsPidMarkedAlive, _warnings.Enqueue, pid, null, markerReap);

    private bool IsPidMarkedAlive(int pid) => _alivePids.Contains(pid);

    private static WatchOwnerMarker MarkerOwnedBy(int pid, TimeSpan beatAge) =>
        new(pid, Environment.MachineName, DaemonHeartbeat.DefaultKind, T0 - beatAge, T0 - beatAge);

    private void WriteReservation(WatchOwnerMarker marker, TimeSpan? age = null)
    {
        var path = $"{WatchOwnerMarker.PathFor(_storeDir)}.{Guid.NewGuid():N}.reap";
        File.WriteAllText(path, JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));
        File.SetLastWriteTimeUtc(
            path, DateTime.UtcNow - (age ?? DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10)));
    }

    private string[] Reservations() =>
        Directory.GetFiles(_storeDir, $"{WatchOwnerMarker.MarkerFileName}.*.reap");

    private void WriteMarker(WatchOwnerMarker marker) =>
        File.WriteAllText(
            WatchOwnerMarker.PathFor(_storeDir),
            JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));

    private void WriteGarbageMarker(TimeSpan age)
    {
        var path = WatchOwnerMarker.PathFor(_storeDir);
        File.WriteAllText(path, "{ \"pid\": 4100, \"hos");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
    }

    private WatchOwnerMarker? ReadMarkerFile()
    {
        var path = WatchOwnerMarker.PathFor(_storeDir);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WatchOwnerMarker>(
                File.ReadAllText(path), SqueezrJson.FileOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => Directory.Delete(_storeDir, recursive: true);
}
