using System.Collections.Concurrent;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Indexing.Watch;

// The adoption contract on the claim itself: a takeover of an identified dead owner records who unblocked the
// store, a steal of a live owner is structurally impossible (the reap refuses a marker that changed hands), and
// two live sessions racing for the same dead claim still end with exactly one adopter.
public sealed class WatchOwnerClaimAdoptionTests : IDisposable
{
    private const int DeadPid = 4100;

    private static readonly DateTime T0 = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _storeDir = TestDirs.NewTempDir();
    private readonly ConcurrentQueue<string> _warnings = [];
    private readonly HashSet<int> _alivePids = [];

    private DateTime _now = T0;

    [Fact]
    public void TakeOver_OfIdentifiedDeadOwner_RecordsAdoptionEventOnTheStore()
    {
        var dead = MarkerOwnedBy(
            DeadPid, Environment.MachineName, beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10));
        WriteMarker(dead);

        var outcome = CreateClaim(pid: 5200).TryAcquire();

        outcome.IsOwner.ShouldBeTrue();
        outcome.TookOverFrom!.Pid.ShouldBe(DeadPid);
        var adopted = WatchAdoptionEventFile.TryRead(_storeDir).ShouldNotBeNull();
        adopted.AdopterPid.ShouldBe(5200);
        adopted.AdopterHost.ShouldBe(Environment.MachineName);
        adopted.AdopterKind.ShouldBe(DaemonHeartbeat.DefaultKind);
        adopted.AdoptedAt.ShouldBe(T0);
        adopted.DisplacedPid.ShouldBe(DeadPid);
        adopted.DisplacedHost.ShouldBe(Environment.MachineName);
        adopted.DisplacedStartedAt.ShouldBe(dead.StartedAt);
        adopted.DisplacedLastBeatAt.ShouldBe(dead.LastBeatAt);
        _warnings.ShouldBeEmpty();
    }

    [Fact]
    public void TakeOver_OfUnreadableMarker_RecordsNoEvent_BecauseThereIsNoOwnerToName()
    {
        WriteGarbageMarker(age: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(15));

        var outcome = CreateClaimWithRealClock().TryAcquire();

        outcome.IsOwner.ShouldBeTrue();
        outcome.TookOverFrom.ShouldBeNull();
        WatchAdoptionEventFile.TryRead(_storeDir).ShouldBeNull();
    }

    [Fact]
    public void FreshAcquire_OnFreeStore_RecordsNoEvent()
    {
        CreateClaim().TryAcquire().IsOwner.ShouldBeTrue();

        WatchAdoptionEventFile.TryRead(_storeDir).ShouldBeNull();
    }

    [Fact]
    public void TryAcquire_UndeletableDeadMarker_HoldsInsteadOfLoopingOnIt()
    {
        var dead = MarkerOwnedBy(
            DeadPid, Environment.MachineName, beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10));
        WriteMarker(dead);

        var outcome = CreateClaim(markerReap: _ => false).TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.StoreUnavailable);
        outcome.IsOwner.ShouldBeFalse();
        outcome.Owner!.Pid.ShouldBe(DeadPid);
        ReadMarkerFile().ShouldBe(dead);
        WatchAdoptionEventFile.TryRead(_storeDir).ShouldBeNull();
        _warnings.ShouldHaveSingleItem().ShouldContain("stale owner marker could not be removed");
    }

    [Fact]
    public void TryAcquire_MarkerReplacedByLiveOwnerWhileWeReaped_NeverStealsTheLiveClaim()
    {
        WriteMarker(MarkerOwnedBy(
            DeadPid, Environment.MachineName, beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10)));
        var winner = MarkerOwnedBy(
            5200, Environment.MachineName, beatAge: TimeSpan.FromSeconds(1), startedAt: T0);
        _alivePids.Add(5200);

        var outcome = CreateClaim(markerReap: _ =>
        {
            WriteMarker(winner);
            return false;
        }).TryAcquire();

        outcome.IsOwner.ShouldBeFalse("a claim that changed hands during the reap belongs to a live process");
        outcome.Owner!.Pid.ShouldBe(5200);
        ReadMarkerFile().ShouldBe(winner);
        WatchAdoptionEventFile.TryRead(_storeDir).ShouldBeNull();
        _warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryAcquire_TwoLiveSessionsRacingOneDeadClaim_ExactlyOneAdopts()
    {
        WriteMarker(MarkerOwnedBy(
            DeadPid, Environment.MachineName, beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10)));
        var ready = new SemaphoreSlim(0);
        var contenderPids = new[] { 5200, 5300 };
        var contenders = contenderPids
            .Select(pid => Task.Run(async () =>
            {
                await ready.WaitAsync();
                return CreateClaim(pid: pid).TryAcquire();
            }))
            .ToArray();

        ready.Release(contenders.Length);
        var outcomes = await Task.WhenAll(contenders);

        var winners = outcomes.Where(outcome => outcome.IsOwner).ToList();
        winners.Count.ShouldBe(1, "two live sessions meeting one dead claim must end with one owner");
        var winner = winners[0];
        if (winner.TookOverFrom is { } dead)
        {
            dead.Pid.ShouldBe(DeadPid);
            var adopted = WatchAdoptionEventFile.TryRead(_storeDir).ShouldNotBeNull();
            adopted.AdopterPid.ShouldBe(winner.Owner!.Pid);
        }
        else
        {
            winner.Owner!.Pid.ShouldBeOneOf(contenderPids);
            WatchAdoptionEventFile.TryRead(_storeDir).ShouldBeNull(
                "winning the free gap between the other contender's reap and create is a fresh claim, not an adoption");
        }

        ReadMarkerFile()!.Pid.ShouldBe(winner.Owner.Pid);
    }

    private WatchOwnerClaim CreateClaim(
        int? pid = null,
        Func<WatchOwnerMarker, bool>? markerCreate = null,
        Func<WatchOwnerMarker?, bool>? markerReap = null) =>
        new(
            _storeDir, DaemonHeartbeat.DefaultKind, () => _now, IsPidMarkedAlive, _warnings.Enqueue, pid,
            markerCreate, markerReap);

    private WatchOwnerClaim CreateClaimWithRealClock(int? pid = null) =>
        new(_storeDir, DaemonHeartbeat.DefaultKind, null, IsPidMarkedAlive, _warnings.Enqueue, pid);

    private bool IsPidMarkedAlive(int pid) => _alivePids.Contains(pid);

    private static WatchOwnerMarker MarkerOwnedBy(int pid, string host, TimeSpan beatAge, DateTime? startedAt = null) =>
        new(pid, host, DaemonHeartbeat.DefaultKind, startedAt ?? T0 - beatAge, T0 - beatAge);

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
