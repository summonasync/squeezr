using System.Text.Json;
using Shouldly;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Indexing.Watch.Supervision;

public sealed class SupervisorLoopTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private const string OwnBinary = "/opt/squeezr/squeezr";

    private readonly List<string> _storeDirs = [];
    private readonly string _root = TestDirs.NewTempDir();

    [Fact]
    public void NoLiveOwner_DesiredRunning_LaunchesTheWatchDaemon()
    {
        var workspace = NewWorkspace("demo");
        var loop = NewLoop(workspace);

        var sweep = loop.RunOnce();

        var store = sweep.Stores.ShouldHaveSingleItem();
        store.Action.ShouldBe(SupervisionAction.Started);
        store.StoreKey.ShouldBe(workspace.StoreKey);
        store.ManagedPid.ShouldNotBeNull();
        store.IndexerState.ShouldBe(WatchIndexerState.None);
        workspace.Launcher.Roots.ShouldBe([workspace.RootPath]);
    }

    [Fact]
    public void LiveOwnerOnTheStore_SkipsAsManagedElsewhere_AndNeverLaunches()
    {
        var workspace = NewWorkspace("demo");
        WriteClaim(
            workspace.StoreDir,
            new WatchOwnerMarker(4242, Environment.MachineName, DaemonHeartbeat.DefaultKind, Now, Now));
        var loop = NewLoop(workspace, pid => pid == 4242);

        var sweep = loop.RunOnce();

        var store = sweep.Stores.ShouldHaveSingleItem();
        store.Action.ShouldBe(SupervisionAction.SkippedLiveOwner);
        store.OwnerPid.ShouldBe(4242);
        store.IndexerState.ShouldBe(WatchIndexerState.Alive);
        workspace.Launcher.Roots.ShouldBeEmpty();
    }

    [Fact]
    public void DesiredStopped_NeverLaunches_AndStopsTheDaemonItAlreadyRuns()
    {
        var workspace = NewWorkspace("demo");
        workspace.State.SetDesire(workspace.StoreKey, WatchDesire.Stopped);
        var loop = NewLoop(workspace);

        loop.RunOnce().Stores[0].Action.ShouldBe(SupervisionAction.LeftStopped);
        workspace.Launcher.Roots.ShouldBeEmpty();

        workspace.State.SetDesire(workspace.StoreKey, WatchDesire.Running);
        loop.RunOnce().Stores[0].Action.ShouldBe(SupervisionAction.Started);
        workspace.Launcher.Roots.Count.ShouldBe(1);
        var launched = workspace.Launcher.Handle;

        workspace.State.SetDesire(workspace.StoreKey, WatchDesire.Stopped);
        loop.RunOnce().Stores[0].Action.ShouldBe(SupervisionAction.StoppedByDesire);
        launched.StopCalled.ShouldBeTrue();
        workspace.Launcher.Roots.Count.ShouldBe(1);
    }

    [Fact]
    public void SixthStartInsideTenMinutes_IsBlocked_AndTheLauncherStopsBeingCalled()
    {
        var workspace = NewWorkspace("demo");
        workspace.Launcher.OnStart = _ => new FakeHandle(exited: true);
        var loop = NewLoop(workspace);

        for (var sweep = 0; sweep < CrashLoopGuard.RestartLimit; sweep++)
        {
            loop.RunOnce().Stores[0].Action.ShouldBe(SupervisionAction.Started);
        }

        var sixth = loop.RunOnce().Stores[0];
        sixth.Action.ShouldBe(SupervisionAction.BlockedByCrashLoop);
        sixth.Detail.ShouldContain("5");
        workspace.Launcher.Roots.Count.ShouldBe(CrashLoopGuard.RestartLimit);

        loop.RunOnce().Stores[0].Action.ShouldBe(SupervisionAction.BlockedByCrashLoop);
        workspace.Launcher.Roots.Count.ShouldBe(CrashLoopGuard.RestartLimit);
    }

    [Fact]
    public void DaemonBinaryNewerThanTheProcessStart_TriggersADriftRestart()
    {
        var workspace = NewWorkspace("demo");
        var starts = 0;
        workspace.Launcher.OnStart = _ => new FakeHandle(
            id: 1000 + ++starts,
            startedUtc: starts == 1 ? Now.AddHours(-2) : Now);
        var loop = NewLoop(workspace, binaryModifiedUtc: _ => Now.AddHours(-1));

        loop.RunOnce().Stores[0].Action.ShouldBe(SupervisionAction.Started);
        var stale = workspace.Launcher.Handle;

        var drifted = loop.RunOnce().Stores[0];
        drifted.Action.ShouldBe(SupervisionAction.DriftRestarted);
        drifted.ManagedPid.ShouldBe(1002);
        stale.StopCalled.ShouldBeTrue();
        workspace.Launcher.Roots.Count.ShouldBe(2);

        var settled = loop.RunOnce().Stores[0];
        settled.Action.ShouldBe(SupervisionAction.LeftRunning);
        workspace.Launcher.Roots.Count.ShouldBe(2);
    }

    [Fact]
    public void DaemonBinaryOlderThanTheProcessStart_KeepsRunning()
    {
        var workspace = NewWorkspace("demo");
        workspace.Launcher.OnStart = _ => new FakeHandle(id: 1001, startedUtc: Now.AddHours(-1));
        var loop = NewLoop(workspace, binaryModifiedUtc: _ => Now.AddHours(-2));

        loop.RunOnce();

        var sweep = loop.RunOnce();
        sweep.Stores[0].Action.ShouldBe(SupervisionAction.LeftRunning);
        workspace.Launcher.Handle.StopCalled.ShouldBeFalse();
        workspace.Launcher.Roots.Count.ShouldBe(1);
    }

    [Fact]
    public void RootMissingOnDisk_SkipsTheStore_WithoutLaunching()
    {
        var workspace = NewWorkspace("demo");
        Directory.Delete(workspace.RootPath, recursive: true);
        var loop = NewLoop(workspace);

        var sweep = loop.RunOnce();

        var store = sweep.Stores.ShouldHaveSingleItem();
        store.Action.ShouldBe(SupervisionAction.SkippedRoot);
        workspace.Launcher.Roots.ShouldBeEmpty();
    }

    [Fact]
    public void RegistryRowWithUnresolvableStoreKey_SkipsWithoutLaunching()
    {
        var workspace = NewWorkspace("demo");
        var entry = new WorkspaceRegistryEntry(
            "sneaky", workspace.RootPath, "../escape", DateTimeOffset.Now, DateTimeOffset.Now);
        var loop = NewLoop([entry]);

        var sweep = loop.RunOnce();

        sweep.Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.SkippedRoot);
        workspace.Launcher.Roots.ShouldBeEmpty();
    }

    [Fact]
    public void ExitedManagedDaemon_IsReplacedOnTheNextSweep()
    {
        var workspace = NewWorkspace("demo");
        workspace.Launcher.OnStart = _ => new FakeHandle(exited: true);
        var loop = NewLoop(workspace);

        loop.RunOnce().Stores[0].Action.ShouldBe(SupervisionAction.Started);

        var sweep = loop.RunOnce();
        sweep.Stores[0].Action.ShouldBe(SupervisionAction.Started);
        workspace.Launcher.Roots.Count.ShouldBe(2);
    }

    [Fact]
    public void RegistryFailure_ReportsAFaultSweep_AndRecoversOnTheNextSweep()
    {
        var workspace = NewWorkspace("demo");
        var failing = true;
        var loop = NewLoop([workspace.Entry], launcher: workspace.Launcher, registry: () =>
        {
            if (failing)
            {
                throw new IOException("registry is unreadable");
            }

            return [workspace.Entry];
        });

        var broken = loop.RunOnce();
        broken.Fault.ShouldBe("registry is unreadable");
        broken.Stores.ShouldBeEmpty();
        workspace.Launcher.Roots.ShouldBeEmpty();

        failing = false;
        var recovered = loop.RunOnce();
        recovered.Fault.ShouldBeNull();
        recovered.Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.Started);
        workspace.Launcher.Roots.Count.ShouldBe(1);
    }

    [Fact]
    public void ProbeFailure_MarksTheStore_AsProbeFailed_AndKeepsTheSweepAlive()
    {
        var workspace = NewWorkspace("demo");
        var failing = true;
        var loop = NewLoop(workspace, health: _ =>
        {
            if (failing)
            {
                throw new IOException("marker is unreadable");
            }

            return WatchStoreHealth.Probe(workspace.StoreDir, Now);
        });

        var sweep = loop.RunOnce();
        sweep.Fault.ShouldBeNull();
        var store = sweep.Stores.ShouldHaveSingleItem();
        store.Action.ShouldBe(SupervisionAction.ProbeFailed);
        store.Detail.ShouldBe("marker is unreadable");
        workspace.Launcher.Roots.ShouldBeEmpty();

        failing = false;
        loop.RunOnce().Stores[0].Action.ShouldBe(SupervisionAction.Started);
        workspace.Launcher.Roots.Count.ShouldBe(1);
    }

    [Fact]
    public void RegistryCancellation_PropagatesInsteadOfBeingSwallowed()
    {
        var workspace = NewWorkspace("demo");
        var loop = NewLoop([workspace.Entry], registry: () => throw new OperationCanceledException());

        Should.Throw<OperationCanceledException>(() => loop.RunOnce());
    }

    private SupervisorLoop NewLoop(
        TestWorkspace workspace,
        Func<int, bool>? isProcessAlive = null,
        Func<string, DateTime>? binaryModifiedUtc = null,
        Func<string, WatchStoreHealth>? health = null,
        SupervisorSelfCheck? selfCheck = null,
        TimeSpan? pollInterval = null) =>
        NewLoop(
            [workspace.Entry],
            workspace.State,
            workspace.Launcher,
            isProcessAlive,
            binaryModifiedUtc,
            health,
            registry: null,
            selfCheck,
            pollInterval);

    private SupervisorLoop NewLoop(
        IReadOnlyList<WorkspaceRegistryEntry> entries,
        SupervisionState? state = null,
        FakeLauncher? launcher = null,
        Func<int, bool>? isProcessAlive = null,
        Func<string, DateTime>? binaryModifiedUtc = null,
        Func<string, WatchStoreHealth>? health = null,
        Func<IReadOnlyList<WorkspaceRegistryEntry>>? registry = null,
        SupervisorSelfCheck? selfCheck = null,
        TimeSpan? pollInterval = null) =>
        new(
            launcher ?? new FakeLauncher(),
            state ?? new SupervisionState(),
            new CrashLoopGuard(),
            registry ?? (() => entries),
            health ?? (storeDir => WatchStoreHealth.Probe(storeDir, Now, isProcessAlive ?? (_ => false))),
            binaryModifiedUtc ?? (_ => Now),
            () => Now,
            pollInterval ?? SupervisorLoop.PollInterval,
            selfCheck);

    [Fact]
    public void OwnBinaryNewerThanTheSupervisorStart_RetiresTheSweep_AndTouchesNoStore()
    {
        var launcher = new FakeLauncher();
        var loop = NewLoop(
            [],
            launcher: launcher,
            binaryModifiedUtc: OwnThen(Now, Now),
            selfCheck: OwnCheck(Now.AddHours(-1), restartsOnUnsuccessfulExit: true));

        loop.RunOnce().OwnDrift.ShouldBeNull();

        var sweep = loop.RunOnce();

        var drift = sweep.OwnDrift.ShouldNotBeNull();
        drift.BinaryPath.ShouldBe(OwnBinary);
        drift.ProcessStartUtc.ShouldBe(Now.AddHours(-1));
        drift.BinaryModifiedUtc.ShouldBe(Now);
        sweep.Stores.ShouldBeEmpty();
        sweep.Fault.ShouldBeNull();
        launcher.Roots.ShouldBeEmpty();
    }

    // review-110-08 BLOCKER-1: children survive the retire by omission — nothing on that path calls
    // Stop() — so this is the pin that fails if somebody adds a stop-all to the exit path.
    [Fact]
    public void OwnBinaryDrift_RetiresWithALiveManagedChild_AndNeverStopsIt()
    {
        var workspace = NewWorkspace("demo");
        workspace.Launcher.OnStart = _ => new FakeHandle(id: 1001, startedUtc: Now.AddHours(-1));
        var loop = NewLoop(
            workspace,
            binaryModifiedUtc: OwnThen(Now, Now.AddHours(-2)),
            selfCheck: OwnCheck(Now.AddHours(-1), restartsOnUnsuccessfulExit: true));

        loop.RunOnce().Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.Started);
        var child = workspace.Launcher.Handle;

        var sweep = loop.RunOnce();

        sweep.OwnDrift.ShouldNotBeNull();
        child.StopCalled.ShouldBeFalse();
        sweep.Stores.ShouldBeEmpty();
        workspace.Launcher.Roots.ShouldBe([workspace.RootPath]);
    }

    // review-110-08 WARN-1, measured 3/3: Process.StartTime is floored on Linux, so a stamp a few
    // milliseconds ahead of the start is not evidence of an upgrade. The first tick has to do its
    // sweep instead of exiting, and only the next one may retire.
    [Fact]
    public void OwnBinaryFiveMillisecondsAheadOfTheStart_SupervisesTheFirstTick_AndRetiresOnTheSecond()
    {
        var workspace = NewWorkspace("demo");
        var loop = NewLoop(
            workspace,
            binaryModifiedUtc: OwnThen(Now.AddMilliseconds(5), Now),
            selfCheck: OwnCheck(Now, restartsOnUnsuccessfulExit: true));

        var first = loop.RunOnce();

        first.OwnDrift.ShouldBeNull("a stamp ahead by milliseconds retired a supervisor that saw no fleet");
        first.Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.Started);

        var second = loop.RunOnce();

        var drift = second.OwnDrift.ShouldNotBeNull();
        drift.ProcessStartUtc.ShouldBe(Now);
        drift.BinaryModifiedUtc.ShouldBe(Now.AddMilliseconds(5));
    }

    [Fact]
    public void OwnBinaryStampExactlyAtTheSupervisorStart_KeepsSupervisingTheFleet()
    {
        var workspace = NewWorkspace("demo");
        var loop = NewLoop(
            workspace,
            binaryModifiedUtc: OwnThen(Now, Now),
            selfCheck: OwnCheck(Now, restartsOnUnsuccessfulExit: true));

        loop.RunOnce();
        var sweep = loop.RunOnce();

        sweep.OwnDrift.ShouldBeNull();
        sweep.Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.LeftRunning);
        workspace.Launcher.Roots.Count.ShouldBe(1);
    }

    [Fact]
    public void RegistryFault_DoesNotCountAsTheSweepThatUnlocksTheRetirement()
    {
        var workspace = NewWorkspace("demo");
        var failing = true;
        var loop = NewLoop(
            [workspace.Entry],
            registry: () =>
            {
                if (failing)
                {
                    throw new IOException("registry is unreadable");
                }

                return [workspace.Entry];
            },
            binaryModifiedUtc: OwnThen(Now, Now),
            selfCheck: OwnCheck(Now.AddHours(-1), restartsOnUnsuccessfulExit: true));

        var broken = loop.RunOnce();
        broken.Fault.ShouldBe("registry is unreadable");
        broken.OwnDrift.ShouldBeNull();

        failing = false;
        loop.RunOnce().Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.Started);

        loop.RunOnce().OwnDrift.ShouldNotBeNull();
    }

    [Fact]
    public void OwnBinaryDrift_WhenTheEntryWillNotStartItAgain_KeepsSupervising()
    {
        var workspace = NewWorkspace("demo");
        var loop = NewLoop(
            workspace,
            binaryModifiedUtc: OwnThen(Now, Now.AddHours(-2)),
            selfCheck: OwnCheck(Now.AddHours(-1), restartsOnUnsuccessfulExit: false));

        var sweep = loop.RunOnce();

        sweep.OwnDrift.ShouldBeNull();
        sweep.Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.Started);
    }

    [Fact]
    public void OwnBinaryOlderThanTheSupervisorStart_KeepsSupervising()
    {
        var workspace = NewWorkspace("demo");
        var loop = NewLoop(
            workspace,
            binaryModifiedUtc: OwnThen(Now.AddHours(-2), Now.AddHours(-2)),
            selfCheck: OwnCheck(Now.AddHours(-1), restartsOnUnsuccessfulExit: true));

        var sweep = loop.RunOnce();

        sweep.OwnDrift.ShouldBeNull();
        sweep.Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.Started);
    }

    [Fact]
    public void OwnStartTimeUnknown_RetirementStaysOffEvenForAFreshBinary()
    {
        var workspace = NewWorkspace("demo");
        var loop = NewLoop(
            workspace,
            binaryModifiedUtc: OwnThen(Now, Now.AddHours(-2)),
            selfCheck: new SupervisorSelfCheck(OwnBinary, null, true));

        var sweep = loop.RunOnce();

        sweep.OwnDrift.ShouldBeNull();
        sweep.Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.Started);
    }

    [Fact]
    public void SelfCheckOn_OwnBinaryCurrent_ChildDriftStillRestartsTheChild()
    {
        var workspace = NewWorkspace("demo");
        workspace.Launcher.OnStart = _ => new FakeHandle(id: 1001, startedUtc: Now.AddHours(-1));
        var loop = NewLoop(
            workspace,
            binaryModifiedUtc: OwnThen(Now.AddHours(-2), Now),
            selfCheck: OwnCheck(Now.AddHours(-1), restartsOnUnsuccessfulExit: true));

        loop.RunOnce();
        var stale = workspace.Launcher.Handle;
        var sweep = loop.RunOnce();

        sweep.OwnDrift.ShouldBeNull();
        sweep.Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.DriftRestarted);
        stale.StopCalled.ShouldBeTrue();
    }

    [Fact]
    public void NoSelfCheck_SupervisesTheStoresAndNeverStatsTheOwnBinary()
    {
        var workspace = NewWorkspace("demo");
        workspace.Launcher.OnStart = _ => new FakeHandle(id: 1001, startedUtc: Now);
        var asked = new List<string>();
        var loop = NewLoop(workspace, binaryModifiedUtc: path =>
        {
            asked.Add(path);
            return Now;
        });

        loop.RunOnce().Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.Started);
        var sweep = loop.RunOnce();

        sweep.OwnDrift.ShouldBeNull();
        sweep.Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.LeftRunning);
        asked.ShouldContain("squeezr-fake-binary");
        asked.ShouldNotContain(OwnBinary);
    }

    [Fact]
    public void Run_ReturnsOwnBinaryDrifted_AndReportsTheRetiringSweepOnce()
    {
        var workspace = NewWorkspace("demo");
        var loop = NewLoop(
            workspace,
            binaryModifiedUtc: OwnThen(Now, Now),
            selfCheck: OwnCheck(Now.AddHours(-1), restartsOnUnsuccessfulExit: true),
            pollInterval: TimeSpan.FromMilliseconds(10));
        var sweeps = new List<SupervisorSweep>();
        using var never = new CancellationTokenSource();

        var reason = loop.Run(never.Token, sweeps.Add);

        reason.ShouldBe(SupervisorExitReason.OwnBinaryDrifted);
        sweeps[0].OwnDrift.ShouldBeNull();
        sweeps[0].Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.Started);
        var retiring = sweeps[1];
        retiring.OwnDrift.ShouldNotBeNull();
        retiring.Stores.ShouldBeEmpty();
        workspace.Launcher.Roots.Count.ShouldBe(1);
    }

    [Fact]
    public void Run_EndsCancelled_WhileTheOwnBinaryIsCurrent()
    {
        var workspace = NewWorkspace("demo");
        var loop = NewLoop(
            workspace,
            binaryModifiedUtc: OwnThen(Now.AddHours(-2), Now.AddHours(-2)),
            selfCheck: OwnCheck(Now.AddHours(-1), restartsOnUnsuccessfulExit: true));
        using var cts = new CancellationTokenSource();
        var sweeps = new List<SupervisorSweep>();

        var reason = loop.Run(cts.Token, sweep =>
        {
            sweeps.Add(sweep);
            cts.Cancel();
        });

        reason.ShouldBe(SupervisorExitReason.Cancelled);
        sweeps.ShouldHaveSingleItem().OwnDrift.ShouldBeNull();
        sweeps[0].Stores.ShouldHaveSingleItem().Action.ShouldBe(SupervisionAction.Started);
    }

    private static SupervisorSelfCheck OwnCheck(DateTime? startedUtc, bool restartsOnUnsuccessfulExit) =>
        new(OwnBinary, startedUtc, restartsOnUnsuccessfulExit);

    private static Func<string, DateTime> OwnThen(DateTime own, DateTime managed) =>
        path => path == OwnBinary ? own : managed;

    private TestWorkspace NewWorkspace(string name)
    {
        var storeKey = $"{name}-{Guid.NewGuid():N}";
        var storeDir = Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
        Directory.CreateDirectory(storeDir);
        _storeDirs.Add(storeDir);
        return new TestWorkspace(
            _root,
            storeKey,
            storeDir,
            new WorkspaceRegistryEntry(name, _root, storeKey, DateTimeOffset.Now, DateTimeOffset.Now),
            new SupervisionState(),
            new FakeLauncher());
    }

    private static void WriteClaim(string storeDir, WatchOwnerMarker marker) =>
        File.WriteAllText(WatchOwnerMarker.PathFor(storeDir), JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
        foreach (var storeDir in _storeDirs)
        {
            Directory.Delete(storeDir, recursive: true);
        }
    }

    private sealed record TestWorkspace(
        string RootPath,
        string StoreKey,
        string StoreDir,
        WorkspaceRegistryEntry Entry,
        SupervisionState State,
        FakeLauncher Launcher);

    private sealed class FakeLauncher : IWatchDaemonLauncher
    {
        public List<string> Roots { get; } = [];

        public FakeHandle Handle { get; private set; } = new();

        public Func<IWatchDaemonHandle, FakeHandle>? OnStart { get; set; }

        public IWatchDaemonHandle StartWatch(string rootPath)
        {
            Roots.Add(rootPath);
            Handle = OnStart?.Invoke(Handle) ?? new FakeHandle(id: 1000 + Roots.Count);
            return Handle;
        }
    }

    private sealed class FakeHandle : IWatchDaemonHandle
    {
        public FakeHandle(int id = 9999, DateTime? startedUtc = null, bool exited = false)
        {
            Id = id;
            StartedUtc = startedUtc;
            HasExited = exited;
        }

        public int Id { get; }

        public string BinaryPath => "squeezr-fake-binary";

        public DateTime? StartedUtc { get; }

        public bool HasExited { get; }

        public bool StopCalled { get; private set; }

        public void Stop() => StopCalled = true;

        public void Dispose()
        {
        }
    }
}
