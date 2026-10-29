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

    private SupervisorLoop NewLoop(
        TestWorkspace workspace,
        Func<int, bool>? isProcessAlive = null,
        Func<string, DateTime>? binaryModifiedUtc = null) =>
        NewLoop([workspace.Entry], workspace.State, workspace.Launcher, isProcessAlive, binaryModifiedUtc);

    private SupervisorLoop NewLoop(
        IReadOnlyList<WorkspaceRegistryEntry> entries,
        SupervisionState? state = null,
        FakeLauncher? launcher = null,
        Func<int, bool>? isProcessAlive = null,
        Func<string, DateTime>? binaryModifiedUtc = null) =>
        new(
            launcher ?? new FakeLauncher(),
            state ?? new SupervisionState(),
            new CrashLoopGuard(),
            () => entries,
            storeDir => WatchStoreHealth.Probe(storeDir, Now, isProcessAlive ?? (_ => false)),
            binaryModifiedUtc ?? (_ => Now),
            () => Now,
            SupervisorLoop.PollInterval);

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
