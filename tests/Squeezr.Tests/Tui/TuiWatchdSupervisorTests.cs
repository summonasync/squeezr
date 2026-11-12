using Shouldly;
using Spectre.Console.Testing;
using Squeezr.Cli.Tui;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Tui;

public sealed class TuiWatchdSupervisorTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir = TestDirs.NewTempDir();
    private readonly List<string> _roots = [];

    [Fact]
    public async Task Run_NoStoresRegistered_ShowsHintAndReturnsOnBack()
    {
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 0);

        await TuiWatchdSupervisor.RunAsync(console, EmptySource(), NewState());

        console.Output.ShouldContain("No stores registered yet");
    }

    [Fact]
    public async Task Run_RendersSessionsTableWithStateHealthAndLastPass()
    {
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 2);

        await TuiWatchdSupervisor.RunAsync(console, FakeSource(NewRow(), NewRow("other", WatchdSessionState.Wedged)), NewState());

        console.Output.ShouldContain("demo");
        console.Output.ShouldContain("running");
        console.Output.ShouldContain("idle");
        console.Output.ShouldContain("no pass recorded");
        console.Output.ShouldContain("other");
        console.Output.ShouldContain("wedged");
    }

    [Fact]
    public async Task Run_DaemonDown_ShowsHonestWarning()
    {
        var console = TuiTestHarness.InteractiveConsole();
        var source = FakeSource(NewRow());
        source.View = source.View with { Daemon = WatchdDaemonStatus.NotRunning };
        TuiTestHarness.PushMenuChoice(console, 1);

        await TuiWatchdSupervisor.RunAsync(console, source, NewState());

        console.Output.ShouldContain("watchd is not running");
    }

    [Fact]
    public async Task StopAction_WritesDesireAndRefreshedListShowsStopped()
    {
        var entry = NewEntry("demo");
        var state = NewState();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 1);

        await TuiWatchdSupervisor.RunAsync(console, RealSource([entry], state), state);

        state.DesireOf(entry.StoreKey).ShouldBe(WatchDesire.Stopped);
        console.Output.ShouldContain("desired state set to stopped");
    }

    [Fact]
    public async Task StartAction_WritesRunningDesire()
    {
        var entry = NewEntry("demo");
        var state = NewState();
        state.SetDesire(entry.StoreKey, WatchDesire.Stopped);
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);

        await TuiWatchdSupervisor.RunAsync(console, RealSource([entry], state), state);

        state.DesireOf(entry.StoreKey).ShouldBe(WatchDesire.Running);
        console.Output.ShouldContain("desired state set to running");
    }

    [Fact]
    public async Task RestartAction_StopsThenRunsDesireAgain()
    {
        var entry = NewEntry("demo");
        var state = NewState();
        var healthCalls = 0;
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 2);
        TuiTestHarness.PushMenuChoice(console, 1);

        var source = RealSource([entry], state, health: _ =>
        {
            healthCalls++;
            // The table render eats the first probe, so the watcher has to still be alive for the
            // panel's own first ask; from the second ask on it is gone and the restart can be requested.
            return healthCalls <= 2
                ? new WatchStoreHealth(WatchIndexerState.Alive, 4711, "watch", null, 0)
                : new WatchStoreHealth(WatchIndexerState.None, null, null, null, 0);
        });
        await TuiWatchdSupervisor.RunAsync(console, source, state);

        state.DesireOf(entry.StoreKey).ShouldBe(WatchDesire.Running);
        healthCalls.ShouldBeGreaterThan(2);
        console.Output.ShouldContain("restart requested");
    }

    [Fact]
    public async Task BackFromPanel_DoesNotTouchDesire()
    {
        var entry = NewEntry("demo");
        var state = NewState();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 3);
        TuiTestHarness.PushMenuChoice(console, 1);

        await TuiWatchdSupervisor.RunAsync(console, RealSource([entry], state), state);

        state.DesireOf(entry.StoreKey).ShouldBe(WatchDesire.Running);
    }

    [Fact]
    public async Task Action_WhenWishCannotBeSaved_ShowsErrorInsteadOfFailing()
    {
        var entry = NewEntry("demo");
        var state = new SupervisionState(Path.Combine(NewBlockedDir(), "watchd", "state.json"));
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 1);

        await TuiWatchdSupervisor.RunAsync(console, RealSource([entry], state), state);

        console.Output.ShouldContain("the wish could not be saved");
    }

    [Fact]
    public async Task DeadDaemon_PanelStillWritesDesire()
    {
        var entry = NewEntry("demo");
        var state = NewState();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 1);

        var source = RealSource([entry], state);
        await TuiWatchdSupervisor.RunAsync(console, source, state);

        console.Output.ShouldContain("watchd is not running — the wish is saved and applies on its start");
        state.DesireOf(entry.StoreKey).ShouldBe(WatchDesire.Stopped);
    }

    [Fact]
    public async Task Run_WantedStoreUnderLiveWatchd_ReadsStartingNotStopped()
    {
        var console = TuiTestHarness.InteractiveConsole();
        var row = NewRow("demo", WatchdSessionState.Running) with { State = WatchdSessionState.Starting };
        TuiTestHarness.PushMenuChoice(console, 1);

        await TuiWatchdSupervisor.RunAsync(console, FakeSource(row), NewState());

        console.Output.ShouldContain("starting");
        console.Output.ShouldNotContain("stopped");
    }

    [Fact]
    public async Task Run_WantedStoreWithoutWatchd_ReadsIdleNoSupervisor_NotStopped()
    {
        var console = TuiTestHarness.InteractiveConsole();
        var row = NewRow("demo", WatchdSessionState.Running) with { State = WatchdSessionState.NoSupervisor };
        TuiTestHarness.PushMenuChoice(console, 1);

        await TuiWatchdSupervisor.RunAsync(console, FakeSource(row), NewState());

        console.Output.ShouldContain("idle (no supervisor)");
        console.Output.ShouldNotContain("stopped");
    }

    [Fact]
    public async Task Run_StateFileUnreadable_ShowsUnknownDesireAndNamesTheFault()
    {
        var entry = NewEntry("demo");
        WriteRawState("{ \"stores\": oops");
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 1);

        await TuiWatchdSupervisor.RunAsync(console, RealSource([entry], NewState()), NewState());

        console.Output.ShouldContain("unknown");
        console.Output.ShouldContain("watchd state is unreadable");
    }

    [Fact]
    public async Task StartAction_OnRowThatDoesNotResolve_Refuses_AndWritesNoDesire()
    {
        var entry = NewEntry("demo");
        Directory.Delete(entry.RootPath, recursive: true);
        var state = NewState();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);

        await TuiWatchdSupervisor.RunAsync(console, RealSource([entry], state), state);

        console.Output.ShouldContain("wish not saved");
        console.Output.ShouldContain("does not resolve on this machine");
        state.Read().Desires.ContainsKey(entry.StoreKey).ShouldBeFalse();
    }

    [Fact]
    public async Task StopAction_OnRowThatDoesNotResolve_Refuses_AndWritesNoDesire()
    {
        var entry = NewEntry("demo");
        Directory.Delete(entry.RootPath, recursive: true);
        var state = NewState();
        var console = TuiTestHarness.InteractiveConsole();
        TuiTestHarness.PushMenuChoice(console, 0);
        TuiTestHarness.PushMenuChoice(console, 1);
        TuiTestHarness.PushMenuChoice(console, 1);

        await TuiWatchdSupervisor.RunAsync(console, RealSource([entry], state), state);

        console.Output.ShouldContain("wish not saved");
        console.Output.ShouldNotContain("desired state set to stopped");
        state.Read().Desires.ContainsKey(entry.StoreKey).ShouldBeFalse();
    }

    private static WatchdSupervisorView EmptyView { get; } = new(WatchdDaemonStatus.NotRunning, []);

    private ScriptedSource EmptySource() => new(EmptyView);

    private static ScriptedSource FakeSource(params WatchdSessionRow[] rows) =>
        new(new WatchdSupervisorView(WatchdDaemonStatus.NotRunning, rows));

    private WatchdSessionRow NewRow() => NewRow("demo", WatchdSessionState.Running);

    private WatchdSessionRow NewRow(string name, WatchdSessionState state) => new(
        $"{name}-abc12345",
        name,
        NewRoot(),
        WatchDesire.Running,
        state,
        "idle",
        "no pass recorded",
        4711);

    private IWatchdSupervisorSource RealSource(
        IReadOnlyList<WorkspaceRegistryEntry> entries,
        SupervisionState state,
        Func<string, WatchStoreHealth>? health = null) =>
        new WatchdSupervisorSource(
            registry: () => entries,
            state: state,
            daemon: new WatchdDaemonProbe(Path.Combine(_dir, "watchd", "absent.lock")),
            health: health ?? (_ => new WatchStoreHealth(WatchIndexerState.Alive, 4711, "watch", null, 0)),
            heartbeat: _ => new DaemonHeartbeat(Environment.ProcessId, DaemonHeartbeat.DefaultKind, Now, Now),
            pass: _ => null,
            utcNow: () => Now);

    private SupervisionState NewState() => new(Path.Combine(_dir, "watchd", "state.json"));

    private string NewBlockedDir()
    {
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "a file where a directory would have to be created");
        return blocker;
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

    private void WriteRawState(string json)
    {
        var path = Path.Combine(_dir, "watchd", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    public void Dispose()
    {
        foreach (var root in _roots.Where(root => Directory.Exists(root)))
        {
            Directory.Delete(root, recursive: true);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private sealed class ScriptedSource(WatchdSupervisorView view) : IWatchdSupervisorSource
    {
        public WatchdSupervisorView View { get; set; } = view;

        public WatchdSupervisorView Read() => View;

        public bool IsWatcherRunning(string storeKey) => false;
    }
}
