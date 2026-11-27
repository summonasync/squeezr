using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Indexing.Watch;

public class WorkspaceWatcherHeartbeatTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task StartAsync_CreatesDefaultHeartbeatFile_StopAsyncDeletesIt()
    {
        var workspace = NewWorkspace();
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            var heartbeatPath = DaemonHeartbeat.PathFor(SqueezrPaths.StoreDir(workspace), DaemonHeartbeat.DefaultKind);
            File.Exists(heartbeatPath).ShouldBeTrue();

            var heartbeat = DaemonHeartbeatReader.TryRead(SqueezrPaths.StoreDir(workspace));
            heartbeat.ShouldNotBeNull();
            heartbeat.Kind.ShouldBe(DaemonHeartbeat.DefaultKind);
            heartbeat.Pid.ShouldBe(Environment.ProcessId);
            DaemonHeartbeatReader.IsAlive(heartbeat, DateTime.UtcNow).ShouldBeTrue();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }

        File.Exists(DaemonHeartbeat.PathFor(SqueezrPaths.StoreDir(workspace), DaemonHeartbeat.DefaultKind))
            .ShouldBeFalse();
        recorder.Faults.ShouldBeEmpty();
    }

    [Fact]
    public async Task StartAsync_WithServeWatchKind_CreatesSeparateKindFile()
    {
        var workspace = NewWorkspace();
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token, daemonKind: DaemonHeartbeat.ServeWatchKind);
        try
        {
            var storeDir = SqueezrPaths.StoreDir(workspace);
            File.Exists(DaemonHeartbeat.PathFor(storeDir, DaemonHeartbeat.ServeWatchKind)).ShouldBeTrue();
            File.Exists(DaemonHeartbeat.PathFor(storeDir, DaemonHeartbeat.DefaultKind)).ShouldBeFalse();
            DaemonHeartbeatReader.TryRead(storeDir, DaemonHeartbeat.ServeWatchKind)!.Kind
                .ShouldBe(DaemonHeartbeat.ServeWatchKind);
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }

        recorder.Faults.ShouldBeEmpty();
    }

    [Fact]
    public async Task IndexPassCompletion_RecreatesHeartbeatFile()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"oldneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            var heartbeatPath = DaemonHeartbeat.PathFor(SqueezrPaths.StoreDir(workspace), DaemonHeartbeat.DefaultKind);
            File.Delete(heartbeatPath);
            File.Exists(heartbeatPath).ShouldBeFalse();

            WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"beatneedle\"; }");
            await UntilAsync(() => recorder.PassesCompleted >= 1);

            File.Exists(heartbeatPath).ShouldBeTrue();
            recorder.Faults.ShouldBeEmpty();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

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

    private static string NewWorkspace()
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
