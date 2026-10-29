using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Tests.Cli;

public sealed class ClaimGraceAwaiterTests : IDisposable
{
    private const int SelfPid = 5000;
    private const int OtherPid = 4100;
    private const string OtherHost = "build-agent-7";
    private static readonly DateTime T0 = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _storeDir = TestDirs.NewTempDir();
    private readonly HashSet<int> _alivePids = [];
    private readonly List<TimeSpan> _delays = [];

    private DateTime _now = T0;

    [Fact]
    public void Await_DeadOwnerWithFreshBeat_WaitsUntilTheBeatStalesThenTakesOver()
    {
        var owner = MarkerOwnedBy(OtherPid, Environment.MachineName, beatAge: TimeSpan.FromSeconds(1));
        WriteMarker(owner);
        var claim = CreateClaim();
        var held = claim.TryAcquire();
        held.Status.ShouldBe(WatchOwnerStatus.OwnerHold);

        var outcome = CreateAwaiter(_ => false).Await(claim, held);

        outcome.IsOwner.ShouldBeTrue();
        outcome.TookOverFrom!.Pid.ShouldBe(OtherPid);
        ReadMarkerFile()!.Pid.ShouldBe(SelfPid);
        _now.ShouldBe(T0 + DaemonHeartbeat.StaleThreshold);
        _delays.ShouldNotBeEmpty();
        _delays.ShouldAllBe(wait => wait <= ClaimGraceAwaiter.PollInterval);
    }

    [Fact]
    public void Await_LiveOwner_ReturnsTheHoldWithoutWaiting()
    {
        _alivePids.Add(OtherPid);
        var owner = MarkerOwnedBy(OtherPid, Environment.MachineName, beatAge: TimeSpan.FromSeconds(1));
        WriteMarker(owner);
        var claim = CreateClaim();
        var held = claim.TryAcquire();
        held.Status.ShouldBe(WatchOwnerStatus.OwnerHold);

        var outcome = CreateAwaiter(_ => true).Await(claim, held);

        outcome.Status.ShouldBe(WatchOwnerStatus.OwnerHold);
        outcome.Owner!.Pid.ShouldBe(OtherPid);
        _delays.ShouldBeEmpty();
        _now.ShouldBe(T0);
        ReadMarkerFile().ShouldBe(owner);
    }

    [Fact]
    public void Await_OwnerComesAliveMidWait_StopsWaitingAndReportsTheHold()
    {
        var owner = MarkerOwnedBy(OtherPid, Environment.MachineName, beatAge: TimeSpan.FromSeconds(1));
        WriteMarker(owner);
        var claim = CreateClaim();
        var held = claim.TryAcquire();
        held.Status.ShouldBe(WatchOwnerStatus.OwnerHold);

        var outcome = CreateAwaiter(_ => _now >= T0 + TimeSpan.FromSeconds(2)).Await(claim, held);

        outcome.Status.ShouldBe(WatchOwnerStatus.OwnerHold);
        outcome.Owner!.Pid.ShouldBe(OtherPid);
        _delays.ShouldBe([ClaimGraceAwaiter.PollInterval, ClaimGraceAwaiter.PollInterval]);
        ReadMarkerFile().ShouldBe(owner);
    }

    [Fact]
    public void Await_MarkerVanishesMidWait_AcquiresImmediately()
    {
        WriteMarker(MarkerOwnedBy(OtherPid, Environment.MachineName, beatAge: TimeSpan.FromSeconds(1)));
        var claim = CreateClaim();
        var held = claim.TryAcquire();
        held.Status.ShouldBe(WatchOwnerStatus.OwnerHold);
        var awaiter = new ClaimGraceAwaiter(() => _now, _ => false, wait =>
        {
            _delays.Add(wait);
            _now += wait;
            File.Delete(WatchOwnerMarker.PathFor(_storeDir));
        });

        var outcome = awaiter.Await(claim, held);

        outcome.IsOwner.ShouldBeTrue();
        outcome.TookOverFrom.ShouldBeNull();
        _delays.ShouldBe([ClaimGraceAwaiter.PollInterval]);
        ReadMarkerFile()!.Pid.ShouldBe(SelfPid);
    }

    private ClaimGraceAwaiter CreateAwaiter(Func<int, bool> isProcessAlive) =>
        new(() => _now, isProcessAlive, wait =>
        {
            _delays.Add(wait);
            _now += wait;
        });

    private WatchOwnerClaim CreateClaim() =>
        new(_storeDir, DaemonHeartbeat.DefaultKind, () => _now, pid => _alivePids.Contains(pid), null, SelfPid);

    private static WatchOwnerMarker MarkerOwnedBy(int pid, string host, TimeSpan beatAge) =>
        new(pid, host, DaemonHeartbeat.DefaultKind, T0 - beatAge, T0 - beatAge);

    private void WriteMarker(WatchOwnerMarker marker) =>
        File.WriteAllText(
            WatchOwnerMarker.PathFor(_storeDir),
            JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));

    private WatchOwnerMarker? ReadMarkerFile()
    {
        var path = WatchOwnerMarker.PathFor(_storeDir);
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize<WatchOwnerMarker>(File.ReadAllText(path), SqueezrJson.FileOptions);
    }

    public void Dispose() => Directory.Delete(_storeDir, recursive: true);
}
