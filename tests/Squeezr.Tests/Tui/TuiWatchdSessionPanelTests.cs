using Shouldly;
using Squeezr.Cli.Tui;
using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Tests.Tui;

public sealed class TuiWatchdSessionPanelTests : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(5);

    private readonly string _dir = TestDirs.NewTempDir();

    private string StatePath => Path.Combine(_dir, "watchd", "state.json");

    [Fact]
    public async Task Restart_WatcherGoesInsideThePollWindow_RequestsTheRestart()
    {
        var state = NewState();
        var console = TuiTestHarness.InteractiveConsole();
        var source = new FakeSource(running: true) { ReleaseAfterPolls = 1 };

        await TuiWatchdSessionPanel.RestartAsync(
            console, "demo-key", state, source, CancellationToken.None, pollLimit: 4, pollInterval: Poll);

        console.Output.ShouldContain("restart requested for demo-key");
        console.Output.ShouldNotContain("restart pending");
        state.DesireOf("demo-key").ShouldBe(WatchDesire.Running);
    }

    [Fact]
    public async Task Restart_PollWindowExpires_PrintsPendingAndNeverSuccess()
    {
        var state = NewState();
        var console = TuiTestHarness.InteractiveConsole();
        var source = new FakeSource(running: true);

        await TuiWatchdSessionPanel.RestartAsync(
            console, "demo-key", state, source, CancellationToken.None, pollLimit: 2, pollInterval: Poll);

        console.Output.ShouldContain("restart pending for demo-key");
        console.Output.ShouldContain("supervisor will converge");
        console.Output.ShouldNotContain("restart requested");
        source.Polls.ShouldBe(3, "the window is spent polling the watcher, not asking once");
        state.DesireOf("demo-key").ShouldBe(WatchDesire.Running);
    }

    [Fact]
    public async Task Restart_NothingWasRunning_IsNotReportedAsARestartRequest()
    {
        var state = NewState();
        var console = TuiTestHarness.InteractiveConsole();

        await TuiWatchdSessionPanel.RestartAsync(
            console, "demo-key", state, new FakeSource(running: false), CancellationToken.None,
            pollLimit: 4, pollInterval: Poll);

        console.Output.ShouldContain("no watcher was running for demo-key");
        console.Output.ShouldNotContain("restart requested");
        console.Output.ShouldNotContain("restart pending");
        state.DesireOf("demo-key").ShouldBe(WatchDesire.Running);
    }

    [Theory]
    [InlineData(TuiWatchdSessionPanel.StartAction)]
    [InlineData(TuiWatchdSessionPanel.StopAction)]
    [InlineData(TuiWatchdSessionPanel.RestartAction)]
    public async Task EveryAction_OnARowThatDoesNotResolve_Refuses_AndWritesNoDesire(string action)
    {
        var state = NewState();
        var console = TuiTestHarness.InteractiveConsole();
        var session = UnresolvedRow("demo-abc12345");

        await TuiWatchdSessionPanel.ApplyAsync(
            console, action, session, state, new FakeSource(running: false), CancellationToken.None);

        console.Output.ShouldContain("wish not saved");
        console.Output.ShouldContain("squeezr drop --store-key demo-abc12345");
        state.Read().Desires.ContainsKey(session.StoreKey).ShouldBeFalse(
            $"the {action} refusal must leave state.json untouched");
        File.Exists(Path.Combine(_dir, "watchd", "state.json")).ShouldBeFalse(
            "no wish means no file created");
    }

    [Fact]
    public async Task StartAction_OnAResolvingRow_WritesTheDesireAsBefore()
    {
        var state = NewState();
        var console = TuiTestHarness.InteractiveConsole();

        await TuiWatchdSessionPanel.ApplyAsync(
            console, TuiWatchdSessionPanel.StartAction, ResolvingRow("demo-abc12345"), state,
            new FakeSource(running: false), CancellationToken.None);

        console.Output.ShouldContain("desired state set to running for demo-abc12345");
        state.DesireOf("demo-abc12345").ShouldBe(WatchDesire.Running);
    }

    [Fact]
    public async Task ApplyAsync_WhenTheRowIsKnown_StillNamesTheDesireFromTheFile()
    {
        var state = NewState();
        state.SetDesire("demo-abc12345", WatchDesire.Stopped);
        var console = TuiTestHarness.InteractiveConsole();

        await TuiWatchdSessionPanel.ApplyAsync(
            console, TuiWatchdSessionPanel.StopAction, ResolvingRow("demo-abc12345"), state,
            new FakeSource(running: false), CancellationToken.None);

        console.Output.ShouldContain("desired state set to stopped");
    }

    private static WatchdSessionRow ResolvingRow(string storeKey) =>
        new(storeKey, "demo", "/repo/demo", WatchDesire.Running, WatchdSessionState.NoSupervisor,
            "idle", "no pass recorded", null);

    private static WatchdSessionRow UnresolvedRow(string storeKey) =>
        new(storeKey, "demo", "/gone/demo", WatchDesire.Running, WatchdSessionState.Stopped,
            "unresolved", "store row does not resolve on this machine", null)
        { RootResolves = false };

    private SupervisionState NewState() => new(StatePath);

    private sealed class FakeSource(bool running) : IWatchdSupervisorSource
    {
        public int ReleaseAfterPolls { get; init; } = int.MaxValue;

        public int Polls { get; private set; }

        public WatchdSupervisorView Read() => new(WatchdDaemonStatus.NotRunning, []);

        public bool IsWatcherRunning(string storeKey)
        {
            Polls++;
            return running && Polls <= ReleaseAfterPolls;
        }
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);
}
