using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Indexing.Watch;

public class WorkspaceWatcherClaimTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QuietSettle = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task ClaimHeldBySomeoneElse_WatcherRunsNoPasses_AndReportsLoss()
    {
        var workspace = NewIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind).TryAcquire().IsOwner.ShouldBeTrue();
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, claim: ClaimAs(storeDir, ForeignPid()));
        await watcher.StartAsync(workspace, cts.Token, daemonKind: DaemonHeartbeat.ServeWatchKind);
        try
        {
            WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"lostclaim3\"; }");

            await UntilAsync(() => recorder.Faults.Any(f => f.Contains("watch ownership lost")));
            await Task.Delay(QuietSettle);
            recorder.PassesCompleted.ShouldBe(0, "the watcher ran a pass on a claim it does not hold");
            new SearchEngine(workspace).Search(Query("lostclaim3")).Matches.ShouldBeEmpty();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }

        new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind).ReadOwner()!.Pid
            .ShouldBe(Environment.ProcessId, "the stopped watcher deleted a claim it no longer held");
    }

    [Fact]
    public async Task ClaimedWatcher_StopAsync_ReleasesClaim()
    {
        var workspace = NewIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var claim = new WatchOwnerClaim(storeDir, DaemonHeartbeat.ServeWatchKind);
        claim.TryAcquire().IsOwner.ShouldBeTrue();
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, claim: claim);
        await watcher.StartAsync(workspace, cts.Token, daemonKind: DaemonHeartbeat.ServeWatchKind);
        File.Exists(WatchOwnerMarker.PathFor(storeDir)).ShouldBeTrue();

        await StopWatcherAsync(watcher, cts);

        File.Exists(WatchOwnerMarker.PathFor(storeDir)).ShouldBeFalse("graceful stop must release the claim");
        recorder.Faults.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClaimlessWatcher_StartAndStop_WritesNoOwnerMarker()
    {
        var workspace = NewIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"freeclaim5\"; }");
            await UntilAsync(() => recorder.PassesCompleted >= 1);
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }

        File.Exists(WatchOwnerMarker.PathFor(storeDir)).ShouldBeFalse(
            "a claim-less watcher must not touch the owner marker");
        recorder.Faults.ShouldBeEmpty();
    }

    [Fact]
    public async Task ClaimLostMidRun_DropsTheKindMirror_AtDemotionNotAtShutdown()
    {
        var workspace = NewIndexedWorkspace();
        var storeDir = SqueezrPaths.StoreDir(workspace);
        var claim = new WatchOwnerClaim(storeDir, DaemonHeartbeat.DefaultKind);
        claim.TryAcquire().IsOwner.ShouldBeTrue();
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, claim: claim);
        await watcher.StartAsync(workspace, cts.Token);
        var mirrorPath = DaemonHeartbeat.PathFor(storeDir, DaemonHeartbeat.DefaultKind);
        try
        {
            await UntilAsync(() => File.Exists(mirrorPath));

            var winner = new WatchOwnerMarker(
                4242, Environment.MachineName, DaemonHeartbeat.DefaultKind, DateTime.UtcNow, DateTime.UtcNow);
            File.WriteAllText(
                WatchOwnerMarker.PathFor(storeDir),
                System.Text.Json.JsonSerializer.Serialize(winner, Squeezr.Core.Serialization.SqueezrJson.FileOptions));

            await UntilAsync(() =>
                recorder.Faults.Any(f => f.Contains("watch ownership lost")) && !File.Exists(mirrorPath));
            File.Exists(DaemonHeartbeat.BeatPathFor(storeDir, DaemonHeartbeat.DefaultKind, Environment.ProcessId))
                .ShouldBeTrue("a demoted process still belongs on the roster of live processes");

            await Task.Delay(QuietSettle);
            File.Exists(mirrorPath).ShouldBeFalse("the demoted watcher re-stamped the mirror it had just dropped");
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    private static WatchOwnerClaim ClaimAs(string storeDir, int pid) =>
        new(storeDir, DaemonHeartbeat.ServeWatchKind, null, null, null, pid);

    private static int ForeignPid() => Environment.ProcessId ^ 0x3FFF;

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }
            await Task.Delay(100);
        }
        condition().ShouldBeTrue($"condition not met within {WaitTimeout.TotalSeconds}s");
    }

    private static async Task StopWatcherAsync(WorkspaceWatcher watcher, CancellationTokenSource cts)
    {
        await cts.CancelAsync();
        var stop = watcher.StopAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!stop.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        stop.IsCompleted.ShouldBeTrue("watcher did not stop within 5s");
    }

    private static SearchQuery Query(string text) => new(text, false, null, null, null, null, null, null);

    private static string NewIndexedWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("repo", "demo");
        SemanticConfigFile.Save(workspace, SemanticConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { DebounceSeconds = 1 }
        });
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"oldneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static void WriteFile(string workspace, string relativePath, string content)
    {
        var fullPath = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private sealed class WatchRecorder : IWatchStatus
    {
        private readonly object _gate = new();
        private readonly List<string> _faults = [];
        private int _completed;

        public int PassesCompleted => Volatile.Read(ref _completed);

        public IReadOnlyList<string> Faults
        {
            get
            {
                lock (_gate)
                {
                    return _faults.ToArray();
                }
            }
        }

        public void PassStarted(int changedPaths)
        {
        }

        public void PassCompleted(IndexResult result) => Interlocked.Increment(ref _completed);

        public void RetryScheduled(TimeSpan delay)
        {
        }

        public void Faulted(string error)
        {
            lock (_gate)
            {
                _faults.Add(error);
            }
        }
    }
}
