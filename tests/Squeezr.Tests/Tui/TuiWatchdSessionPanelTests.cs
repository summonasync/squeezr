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
