using Shouldly;
using Squeezr.Cli.Tui;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Tui;

public sealed class WatchdSupervisorSourceTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = TestDirs.NewTempDir();
    private readonly List<string> _roots = [];
    private WatchIndexerState _healthState = WatchIndexerState.Alive;

    [Fact]
    public void UnknownStore_DesireRunning()
    {
        var state = NewState();
        state.DesireOf(NewEntry("demo").StoreKey).ShouldBe(WatchDesire.Running);
    }

    [Fact]
    public void LiveOwner_SessionRunning()
    {
        var view = ReadWithDesire(WatchDesire.Running);

        var session = view.Sessions.ShouldHaveSingleItem();
        session.State.ShouldBe(WatchdSessionState.Running);
        session.OwnerPid.ShouldBe(4711);
        session.Health.ShouldBe("idle");
    }

    [Fact]
    public void DesiredStopped_SessionStoppedEvenWhenOwnerStillLive()
    {
        var view = ReadWithDesire(WatchDesire.Stopped);

        view.Sessions.ShouldHaveSingleItem().State.ShouldBe(WatchdSessionState.Stopped);
    }

    [Fact]
    public void ForeignOwner_ManagedElsewhere()
    {
        _healthState = WatchIndexerState.Foreign;

        ReadWithDesire(WatchDesire.Running).Sessions.ShouldHaveSingleItem()
            .State.ShouldBe(WatchdSessionState.ManagedElsewhere);
    }

    [Fact]
    public void WedgedOwner_Wedged()
    {
        _healthState = WatchIndexerState.Wedged;

        ReadWithDesire(WatchDesire.Running).Sessions.ShouldHaveSingleItem()
            .State.ShouldBe(WatchdSessionState.Wedged);
    }

    [Fact]
    public void GoneOwner_WithoutSupervisor_IdleNoSupervisor()
    {
        _healthState = WatchIndexerState.Gone;

        ReadWithDesire(WatchDesire.Running).Sessions.ShouldHaveSingleItem()
            .State.ShouldBe(WatchdSessionState.NoSupervisor);
    }

    // The wording gap review-113-t4-01 obstacle 5 carried here: under a live supervisor the same dead body is
    // starting, not stopped — stopped stays the word for a wish the operator wrote.
    [Fact]
    public void GoneOwner_UnderLiveWatchd_ReadsStartingNotStopped()
    {
        _healthState = WatchIndexerState.Gone;

        var session = ReadWithDesire(WatchDesire.Running, liveWatchd: true).Sessions.ShouldHaveSingleItem();
        session.State.ShouldBe(WatchdSessionState.Starting);
        WatchdSessionText.State(session.State).ShouldBe("starting");
    }

    // R5 in one pair: the same dead body reads stopped when the operator stopped it and idle when nothing
    // is left to carry the wish.
    [Fact]
    public void NothingAlive_NoWatchd_DesireSplitsStoppedFromIdle()
    {
        _healthState = WatchIndexerState.None;

        ReadWithDesire(WatchDesire.Stopped).Sessions.ShouldHaveSingleItem()
            .State.ShouldBe(WatchdSessionState.Stopped);
        ReadWithDesire(WatchDesire.Running).Sessions.ShouldHaveSingleItem()
            .State.ShouldBe(WatchdSessionState.NoSupervisor);
    }

    [Fact]
    public void LiveOwner_UnderLiveWatchd_StillRunning()
    {
        ReadWithDesire(WatchDesire.Running, liveWatchd: true).Sessions.ShouldHaveSingleItem()
            .State.ShouldBe(WatchdSessionState.Running);
    }

    [Fact]
    public void UnreadableStateJson_DoesNotResolveToARunningWish()
    {
        var entry = NewEntry("demo");
        WriteRawState("{ \"stores\": oops");

        var view = BuildSource(WatchIndexerState.Alive, [entry]).Read();

        view.StateFault.ShouldNotBeNull();
        view.StateFault.ShouldContain("not valid JSON");
        var session = view.Sessions.ShouldHaveSingleItem();
        session.DesireKnown.ShouldBeFalse();
        session.RootResolves.ShouldBeTrue();
    }

    [Fact]
    public void ReadableStateJson_LeavesTheFaultOut_AndTheDesireKnown()
    {
        var entry = NewEntry("demo");
        var state = NewState();
        state.SetDesire(entry.StoreKey, WatchDesire.Stopped);

        var view = BuildSource(WatchIndexerState.Alive, [entry], state: state).Read();

        view.StateFault.ShouldBeNull();
        var session = view.Sessions.ShouldHaveSingleItem();
        session.DesireKnown.ShouldBeTrue();
        session.Desire.ShouldBe(WatchDesire.Stopped);
    }

    [Fact]
    public void StoreRowOutsideMachine_UnresolvedAndCannotReceiveAWish()
    {
        var root = NewRoot();
        var entry = new WorkspaceRegistryEntry("demo", root, "bad/key", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var view = BuildSource(healthState: WatchIndexerState.Alive, [entry]).Read();

        var session = view.Sessions.ShouldHaveSingleItem();
        session.State.ShouldBe(WatchdSessionState.Stopped);
        session.Health.ShouldBe("unresolved");
        session.RootResolves.ShouldBeFalse();
    }

    [Fact]
    public void StoreRowWithGoneRoot_CannotReceiveAWish()
    {
        var entry = NewEntry("demo");
        var root = entry.RootPath;
        Directory.Delete(root, recursive: true);

        var session = BuildSource(WatchIndexerState.Alive, [entry]).Read().Sessions.ShouldHaveSingleItem();

        session.Health.ShouldBe("unresolved");
        session.RootResolves.ShouldBeFalse();
    }

    [Fact]
    public void MissingPass_ReportsNoPassRecorded()
    {
        ReadWithDesire(WatchDesire.Running).Sessions.ShouldHaveSingleItem()
            .LastPass.ShouldBe("no pass recorded");
    }

    [Fact]
    public void RunningPass_LastPassCarriesPhaseAndBatch()
    {
        var view = BuildSource(
            WatchIndexerState.Alive,
            [NewEntry("demo")],
            pass: new PassPhaseStamp(Environment.ProcessId, PassPhaseStamp.RunningPhase, Now.AddMinutes(-5), Now.AddSeconds(-30), 12))
            .Read();

        view.Sessions.ShouldHaveSingleItem().LastPass.ShouldBe("running, batch 12, progress under a minute ago");
    }

    [Fact]
    public void WorkingPass_HealthReportsWorking()
    {
        var view = BuildSource(
            WatchIndexerState.Alive,
            [NewEntry("demo")],
            pass: new PassPhaseStamp(Environment.ProcessId, PassPhaseStamp.RunningPhase, Now.AddMinutes(-5), Now.AddSeconds(-30), 12))
            .Read();

        view.Sessions.ShouldHaveSingleItem().Health.ShouldBe("working");
    }

    [Fact]
    public void Sessions_SortedByName()
    {
        var first = NewEntry("beta");
        var second = NewEntry("alpha");

        var view = BuildSource(WatchIndexerState.Alive, [first, second]).Read();

        view.Sessions.Select(session => session.Name).ShouldBe(["alpha", "beta"]);
    }

    [Fact]
    public void IsWatcherRunning_MirrorsHealthIndexerRunning()
    {
        var entry = NewEntry("demo");
        var source = BuildSource(WatchIndexerState.Alive, [entry]);
        source.IsWatcherRunning(entry.StoreKey).ShouldBeTrue();

        _healthState = WatchIndexerState.None;
        var stopped = BuildSource(WatchIndexerState.None, [entry]);
        stopped.IsWatcherRunning(entry.StoreKey).ShouldBeFalse();
    }

    [Fact]
    public void DaemonWithoutLock_ReportedNotRunning()
    {
        var view = BuildSource(WatchIndexerState.Alive, [NewEntry("demo")]).Read();

        view.Daemon.Running.ShouldBeFalse();
    }

    private WatchdSupervisorView ReadWithDesire(WatchDesire desire, bool liveWatchd = false)
    {
        var entry = NewEntry("demo");
        var state = NewState();
        state.SetDesire(entry.StoreKey, desire);
        return BuildSource(_healthState, [entry], state: state, liveWatchd: liveWatchd).Read();
    }

    private WatchdSupervisorSource BuildSource(
        WatchIndexerState healthState,
        IReadOnlyList<WorkspaceRegistryEntry> entries,
        SupervisionState? state = null,
        PassPhaseStamp? pass = null,
        bool liveWatchd = false)
    {
        _healthState = healthState;
        var heartbeat = new DaemonHeartbeat(Environment.ProcessId, DaemonHeartbeat.DefaultKind, Now, Now);
        return new WatchdSupervisorSource(
            registry: () => entries,
            state: state ?? NewState(),
            daemon: liveWatchd
                ? new WatchdDaemonProbe(LiveWatchdLockPath(), _ => true)
                : new WatchdDaemonProbe(Path.Combine(_dir, "watchd", "absent.lock")),
            health: _ => new WatchStoreHealth(healthState, 4711, "watch", null, 0),
            heartbeat: _ => heartbeat,
            pass: _ => pass,
            utcNow: () => Now);
    }

    private string LiveWatchdLockPath()
    {
        var path = Path.Combine(_dir, "watchd", "live.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"pid\":4321,\"host\":\"anywhere\"}");
        return path;
    }

    private WorkspaceRegistryEntry NewEntry(string name)
    {
        var root = NewRoot();
        return new WorkspaceRegistryEntry(
            name, root, WorkspaceStoreKeys.Create(name, root), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

    private string NewRoot()
    {
        var root = TestDirs.NewTempDir();
        _roots.Add(root);
        return root;
    }

    private SupervisionState NewState() => new(Path.Combine(_dir, "watchd", "state.json"));

    private void WriteRawState(string json)
    {
        var path = Path.Combine(_dir, "watchd", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    public void Dispose()
    {
        foreach (var root in _roots.Where(Directory.Exists))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.Delete(_dir, recursive: true);
    }
}
