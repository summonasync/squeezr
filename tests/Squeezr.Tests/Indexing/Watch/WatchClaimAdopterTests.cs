using Shouldly;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Tests.Indexing.Watch;

public sealed class WatchClaimAdopterTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(20);

    private readonly string _storeDir = TestDirs.NewTempDir();
    private readonly HashSet<int> _alivePids = [];

    private DateTime _now = T0;
    private bool _ownerAlive;

    [Fact]
    public async Task OwnerWedgedUnderLivePid_PollsNeverAdopt()
    {
        WriteDeadOwnerMarker();
        _ownerAlive = true;
        var adoptions = 0;
        await using var adopter = CreateAdopter(_ => { adoptions++; return Task.CompletedTask; });
        adopter.Start();

        await Task.Delay(Poll * 10);

        adoptions.ShouldBe(0, "a stale beat under a live pid is a wedged owner, never an adoption target");
        ReadOwnerPid().ShouldBe(DeadPid);
    }

    [Fact]
    public async Task OwnerDiesBetweenPolls_NextPollAdoptsAndStops()
    {
        WriteDeadOwnerMarker();
        _ownerAlive = true;
        var adoptedOutcome = (WatchOwnerOutcome?)null;
        await using var adopter = CreateAdopter(outcome =>
        {
            adoptedOutcome = outcome;
            return Task.CompletedTask;
        });
        adopter.Start();

        await Task.Delay(Poll * 6);
        adoptedOutcome.ShouldBeNull("no adoption may happen while the owner is still alive");

        _ownerAlive = false;

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (adoptedOutcome is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        adoptedOutcome.ShouldNotBeNull();
        adoptedOutcome.Value.IsOwner.ShouldBeTrue();
        adoptedOutcome.Value.TookOverFrom!.Pid.ShouldBe(DeadPid);
        ReadOwnerPid().ShouldBe(Environment.ProcessId);
        WatchAdoptionEventFile.TryRead(_storeDir).ShouldNotBeNull("a runtime adoption records its event like any takeover");

        await Task.Delay(Poll * 5);
        ReadOwnerPid().ShouldBe(Environment.ProcessId, "the adopter is single-shot and must not re-adopt over itself");
    }

    [Fact]
    public async Task Start_Twice_Throws()
    {
        await using var adopter = CreateAdopter(_ => Task.CompletedTask);
        adopter.Start();

        Should.Throw<InvalidOperationException>(() => adopter.Start());
    }

    [Fact]
    public async Task Dispose_BeforeStart_QuietNoOp()
    {
        await using var adopter = CreateAdopter(_ => Task.CompletedTask);

        await adopter.DisposeAsync();
    }

    private int DeadPid { get; } = 4100;

    private WatchClaimAdopter CreateAdopter(Func<WatchOwnerOutcome, Task> onAdopted)
    {
        var claim = new WatchOwnerClaim(
            _storeDir, DaemonHeartbeat.ServeWatchKind, () => _now, IsOwnerAlive, null,
            Environment.ProcessId);
        return new WatchClaimAdopter(claim, onAdopted, Poll);
    }

    private void WriteDeadOwnerMarker()
    {
        var marker = new WatchOwnerMarker(
            DeadPid, Environment.MachineName, DaemonHeartbeat.ServeWatchKind,
            T0 - TimeSpan.FromMinutes(2), T0 - DaemonHeartbeat.StaleThreshold - TimeSpan.FromSeconds(10));
        WatchOwnerMarkerFile.Overwrite(WatchOwnerMarker.PathFor(_storeDir), marker);
    }

    private bool IsOwnerAlive(int pid) => pid == DeadPid ? _ownerAlive : pid == Environment.ProcessId;

    private int ReadOwnerPid() =>
        WatchOwnerMarker.TryRead(_storeDir)?.Pid ?? 0;

    public void Dispose() => Directory.Delete(_storeDir, recursive: true);
}
