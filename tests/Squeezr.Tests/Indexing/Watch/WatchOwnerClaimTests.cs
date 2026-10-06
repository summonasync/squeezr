using System.Collections.Concurrent;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Indexing.Watch;

public sealed class WatchOwnerClaimTests : IDisposable
{
    private const int OtherPid = 4100;
    private const string OtherHost = "build-agent-7";
    private static readonly DateTime T0 = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _storeDir = TestDirs.NewTempDir();
    private readonly ConcurrentQueue<string> _warnings = [];
    private readonly HashSet<int> _alivePids = [];

    private DateTime _now = T0;

    [Fact]
    public void TryAcquire_OnFreeStore_WritesMarkerNamingThisProcess()
    {
        var claim = CreateClaim();

        var outcome = claim.TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.Acquired);
        outcome.IsOwner.ShouldBeTrue();
        outcome.Owner.ShouldNotBeNull();
        outcome.Owner.Pid.ShouldBe(Environment.ProcessId);
        outcome.Owner.Host.ShouldBe(Environment.MachineName);
        outcome.Owner.Kind.ShouldBe(DaemonHeartbeat.DefaultKind);
        outcome.Owner.StartedAt.ShouldBe(T0);
        outcome.Owner.LastBeatAt.ShouldBe(T0);
        File.Exists(claim.MarkerPath).ShouldBeTrue();
    }

    [Fact]
    public async Task TryAcquire_ConcurrentAcquirersOnOneStore_ExactlyOneAcquires()
    {
        var ready = new SemaphoreSlim(0);
        var contenders = Enumerable.Range(0, 8)
            .Select(index => Task.Run(async () =>
            {
                var claim = CreateClaim(pid: OtherPid + index);
                await ready.WaitAsync();
                return claim.TryAcquire();
            }))
            .ToArray();

        ready.Release(contenders.Length);
        var outcomes = await Task.WhenAll(contenders);

        outcomes.Count(outcome => outcome.IsOwner).ShouldBe(1);
        var winner = outcomes.Single(outcome => outcome.IsOwner);
        ReadMarkerFile()!.Pid.ShouldBe(winner.Owner!.Pid);
        foreach (var loser in outcomes.Where(outcome => !outcome.IsOwner))
        {
            loser.Status.ShouldBeOneOf(
                WatchOwnerStatus.OwnerHold,
                WatchOwnerStatus.MarkerUnreadable,
                WatchOwnerStatus.RaceLost);
        }
    }

    [Fact]
    public void TryAcquire_MarkerHeldByLiveOwner_ReportsHoldWithOwnerIdentity()
    {
        _alivePids.Add(OtherPid);
        WriteMarker(MarkerOwnedBy(OtherPid, Environment.MachineName, beatAge: TimeSpan.FromSeconds(2)));

        var outcome = CreateClaim().TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.OwnerHold);
        outcome.IsOwner.ShouldBeFalse();
        outcome.Owner!.Pid.ShouldBe(OtherPid);
    }

    [Fact]
    public void TryAcquire_DeadOwnerWithFreshBeat_KeepsClaimUntilTheStaleWindowPasses()
    {
        WriteMarker(MarkerOwnedBy(OtherPid, Environment.MachineName, beatAge: TimeSpan.FromSeconds(1)));

        var outcome = CreateClaim().TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.OwnerHold);
        outcome.Owner!.Pid.ShouldBe(OtherPid);
    }

    [Fact]
    public void TryAcquire_BeatPastStaleWindowAndPidGone_TakesOver()
    {
        WriteMarker(MarkerOwnedBy(
            OtherPid,
            Environment.MachineName,
            beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10)));

        var outcome = CreateClaim().TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.Acquired);
        outcome.TookOverFrom!.Pid.ShouldBe(OtherPid);
        ReadMarkerFile()!.Pid.ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public void TryAcquire_BeatPastStaleWindowButPidAlive_KeepsClaimWithWedgedOwner()
    {
        _alivePids.Add(OtherPid);
        var owner = MarkerOwnedBy(
            OtherPid,
            Environment.MachineName,
            beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(15));
        WriteMarker(owner);

        var outcome = CreateClaim().TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.OwnerWedged);
        outcome.IsOwner.ShouldBeFalse();
        outcome.Owner!.Pid.ShouldBe(OtherPid);
        ReadMarkerFile().ShouldBe(owner);
    }

    [Fact]
    public void TryAcquire_ForeignHostMarkerFresh_DoesNotTakeOverOnLocalPidEvidence()
    {
        _alivePids.Add(OtherPid);
        var owner = MarkerOwnedBy(OtherPid, OtherHost, beatAge: TimeSpan.FromSeconds(1));
        WriteMarker(owner);

        var outcome = CreateClaim().TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.ForeignOwnerHold);
        outcome.Owner!.Host.ShouldBe(OtherHost);
        ReadMarkerFile().ShouldBe(owner);
    }

    [Fact]
    public void TryAcquire_ForeignHostMarkerPastStaleWindowButInsideExtendedWindow_DoesNotTakeOver()
    {
        var owner = MarkerOwnedBy(
            OtherPid,
            OtherHost,
            beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(1));
        WriteMarker(owner);

        var outcome = CreateClaim().TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.ForeignOwnerHold);
        ReadMarkerFile().ShouldBe(owner);
    }

    [Fact]
    public void TryAcquire_ForeignHostMarkerPastExtendedWindow_TakesOver()
    {
        WriteMarker(MarkerOwnedBy(
            OtherPid,
            OtherHost,
            beatAge: WatchOwnerMarker.ForeignHostStaleWindow + TimeSpan.FromSeconds(1)));

        var outcome = CreateClaim().TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.Acquired);
        outcome.TookOverFrom!.Host.ShouldBe(OtherHost);
        ReadMarkerFile()!.Host.ShouldBe(Environment.MachineName);
    }

    [Fact]
    public void TryAcquire_CorruptMarkerInsideStaleWindow_DoesNotTakeOver()
    {
        WriteGarbageMarker(age: TimeSpan.FromSeconds(1));

        var outcome = CreateClaimWithRealClock().TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.MarkerUnreadable);
        outcome.IsOwner.ShouldBeFalse();
        ReadMarkerFile().ShouldBeNull();
    }

    [Fact]
    public void TryAcquire_CorruptMarkerPastStaleWindow_TakesOver()
    {
        WriteGarbageMarker(age: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(15));

        var outcome = CreateClaimWithRealClock().TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.Acquired);
        outcome.TookOverFrom.ShouldBeNull();
        ReadMarkerFile()!.Pid.ShouldBe(Environment.ProcessId);
    }

    [Fact]
    public void TryAcquire_SameProcessAgain_KeepsOriginalClaimIdentity()
    {
        var claim = CreateClaim();
        claim.TryAcquire();
        _now = T0 + TimeSpan.FromSeconds(10);

        var outcome = claim.TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.Acquired);
        outcome.Owner!.StartedAt.ShouldBe(T0);
    }

    [Fact]
    public void TryAcquire_AnotherProcessCreatesWhileWeReap_ReportsRaceLostNamingTheirMarker()
    {
        WriteMarker(MarkerOwnedBy(
            OtherPid,
            Environment.MachineName,
            beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(10)));
        var contender = MarkerOwnedBy(OtherPid + 1, Environment.MachineName, beatAge: TimeSpan.FromSeconds(1));
        _alivePids.Add(contender.Pid);

        var outcome = CreateClaim(markerCreate: _ =>
        {
            WatchOwnerMarkerFile.TryCreate(WatchOwnerMarker.PathFor(_storeDir), contender);
            return false;
        }).TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.RaceLost);
        outcome.IsOwner.ShouldBeFalse();
        outcome.Owner!.Pid.ShouldBe(contender.Pid);
        ReadMarkerFile()!.Pid.ShouldBe(contender.Pid);
        _warnings.ShouldBeEmpty();
    }

    [Fact]
    public void TryAcquire_MarkerGoneBetweenCreateAndRead_ReportsRaceLostWithoutOwner()
    {
        var path = WatchOwnerMarker.PathFor(_storeDir);
        var contender = MarkerOwnedBy(OtherPid, Environment.MachineName, beatAge: TimeSpan.FromSeconds(1));
        _alivePids.Add(contender.Pid);

        var outcome = CreateClaim(markerCreate: _ =>
        {
            WatchOwnerMarkerFile.TryCreate(path, contender);
            WatchOwnerMarkerFile.TryDelete(path);
            return false;
        }).TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.RaceLost);
        outcome.Owner.ShouldBeNull();
        File.Exists(path).ShouldBeFalse();
    }

    [Fact]
    public void TryAcquire_RetryAfterRaceLost_AcquiresOnceTheStoreIsFree()
    {
        CreateClaim(markerCreate: _ => false).TryAcquire()
            .Status.ShouldBe(WatchOwnerStatus.RaceLost);

        CreateClaim().TryAcquire().Status.ShouldBe(WatchOwnerStatus.Acquired);
    }

    [Fact]
    public void TryAcquire_MarkerBeatFromTheFuture_HoldsUntilTheClockPassesTheSkewedBeat()
    {
        var skew = TimeSpan.FromMinutes(10);
        var owner = MarkerOwnedBy(OtherPid, Environment.MachineName, beatAge: -skew);
        WriteMarker(owner);
        var claim = CreateClaim();

        _now = T0 + DaemonHeartbeat.StaleThreshold;
        claim.TryAcquire().Status.ShouldBe(WatchOwnerStatus.OwnerHold);

        _now = T0 + WatchOwnerMarker.ForeignHostStaleWindow;
        claim.TryAcquire().Status.ShouldBe(WatchOwnerStatus.OwnerHold);
        ReadMarkerFile().ShouldBe(owner);

        _now = T0 + skew + DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(1);
        claim.TryAcquire().Status.ShouldBe(WatchOwnerStatus.Acquired);
    }

    [Fact]
    public void TryAcquire_ForeignHostMarkerFromTheFuture_HoldsPastTheExtendedWindow()
    {
        var owner = MarkerOwnedBy(OtherPid, OtherHost, beatAge: -TimeSpan.FromHours(2));
        WriteMarker(owner);

        _now = T0 + WatchOwnerMarker.ForeignHostStaleWindow * 2;
        var outcome = CreateClaim().TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.ForeignOwnerHold);
        outcome.Owner!.Host.ShouldBe(OtherHost);
        ReadMarkerFile().ShouldBe(owner);
    }

    [Fact]
    public void TryAcquire_StoreDirMissing_ReportsStoreUnavailable()
    {
        var claim = new WatchOwnerClaim(
            Path.Combine(_storeDir, "not-created"),
            onWarning: _warnings.Enqueue);

        var outcome = claim.TryAcquire();

        outcome.Status.ShouldBe(WatchOwnerStatus.StoreUnavailable);
        outcome.IsOwner.ShouldBeFalse();
        _warnings.ShouldHaveSingleItem().ShouldContain("owner marker cannot be written");
    }

    [Fact]
    public void Release_DeletesMarkerSoAnotherProcessAcquiresImmediately()
    {
        var claim = CreateClaim();
        claim.TryAcquire();

        claim.Release().ShouldBeTrue();

        File.Exists(claim.MarkerPath).ShouldBeFalse();
        CreateClaim(pid: OtherPid).TryAcquire().Status.ShouldBe(WatchOwnerStatus.Acquired);
    }

    [Fact]
    public void Release_ThenSameClaimAcquiresAgain()
    {
        var claim = CreateClaim();
        claim.TryAcquire();
        claim.Release();

        claim.TryAcquire().Status.ShouldBe(WatchOwnerStatus.Acquired);
    }

    [Fact]
    public void Release_WhileAnotherProcessHoldsClaim_KeepsTheirMarker()
    {
        _alivePids.Add(OtherPid);
        var owner = MarkerOwnedBy(OtherPid, Environment.MachineName, beatAge: TimeSpan.FromSeconds(1));
        WriteMarker(owner);

        CreateClaim().Release().ShouldBeFalse();

        ReadMarkerFile().ShouldBe(owner);
    }

    [Fact]
    public void Release_OnFreeStore_ReportsNothingReleased() => CreateClaim().Release().ShouldBeFalse();

    [Fact]
    public void RefreshBeat_WhileOwner_KeepsStartedAtAndMovesLastBeatAt()
    {
        var claim = CreateClaim();
        claim.TryAcquire();
        _now = T0 + DaemonHeartbeat.BeatInterval;

        var outcome = claim.RefreshBeat();

        outcome.Status.ShouldBe(WatchOwnerStatus.Acquired);
        outcome.Owner!.StartedAt.ShouldBe(T0);
        outcome.Owner.LastBeatAt.ShouldBe(_now);
    }

    [Fact]
    public void RefreshBeat_WhileAnotherProcessOwns_LeavesTheirMarkerUntouched()
    {
        _alivePids.Add(OtherPid);
        var owner = MarkerOwnedBy(OtherPid, Environment.MachineName, beatAge: TimeSpan.FromSeconds(1));
        WriteMarker(owner);

        var outcome = CreateClaim().RefreshBeat();

        outcome.Status.ShouldBe(WatchOwnerStatus.OwnerHold);
        ReadMarkerFile().ShouldBe(owner);
    }

    [Fact]
    public void RefreshBeat_WhenRecordedOwnerIsStaleAndGone_TakesOverThroughAcquireRules()
    {
        WriteMarker(MarkerOwnedBy(
            OtherPid,
            Environment.MachineName,
            beatAge: DaemonHeartbeat.StaleThreshold + TimeSpan.FromSeconds(5)));

        var outcome = CreateClaim().RefreshBeat();

        outcome.Status.ShouldBe(WatchOwnerStatus.Acquired);
        outcome.TookOverFrom!.Pid.ShouldBe(OtherPid);
    }

    [Fact]
    public void RefreshBeat_OnContestedStore_ReportsRaceLostAsClaimLost()
    {
        var outcome = CreateClaim(markerCreate: _ => false).RefreshBeat();

        outcome.Status.ShouldBe(WatchOwnerStatus.RaceLost);
        outcome.IsOwner.ShouldBeFalse();
    }

    [Fact]
    public void ReadOwner_OnFreeStore_ReturnsNull() => CreateClaim().ReadOwner().ShouldBeNull();

    [Fact]
    public void ReadOwner_OnCorruptMarker_ReturnsNullInsteadOfThrowing()
    {
        WriteGarbageMarker(age: TimeSpan.FromSeconds(1));

        CreateClaim().ReadOwner().ShouldBeNull();
    }

    private WatchOwnerClaim CreateClaim(
        int? pid = null,
        Func<WatchOwnerMarker, bool>? markerCreate = null) =>
        new(_storeDir, DaemonHeartbeat.DefaultKind, () => _now, IsPidMarkedAlive, _warnings.Enqueue, pid, markerCreate);

    private WatchOwnerClaim CreateClaimWithRealClock(int? pid = null) =>
        new(_storeDir, DaemonHeartbeat.DefaultKind, null, IsPidMarkedAlive, _warnings.Enqueue, pid);

    private bool IsPidMarkedAlive(int pid) => _alivePids.Contains(pid);

    private static WatchOwnerMarker MarkerOwnedBy(int pid, string host, TimeSpan beatAge) =>
        new(pid, host, DaemonHeartbeat.DefaultKind, T0 - beatAge, T0 - beatAge);

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
