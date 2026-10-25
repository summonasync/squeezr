using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Watch;

public class WorkspaceWatcherActivityTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task ProgresslessWatcher_PublishesPhaseActivityIntoItsBeat_AndSettlesBackToIdle()
    {
        var workspace = NewWorkspace();
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            for (var i = 0; i < 8; i++)
            {
                WriteFile(workspace, $"repo/file{i}.cs", $"class Demo{i} {{ void Run{i}() => Console.WriteLine({i}); }}");
            }

            await UntilAsync(() => ReadBeat(workspace)?.Activity is not null);
            await UntilAsync(() => recorder.PassesCompleted >= 1);

            ReadBeat(workspace).ShouldNotBeNull().Activity.ShouldBeNull(
                "once the pass completes, RepoCompleted must settle the beat back to idle");
            recorder.Faults.ShouldBeEmpty();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    private static DaemonHeartbeat? ReadBeat(string workspace)
    {
        var beatPath = DaemonHeartbeat.BeatPathFor(
            SqueezrPaths.StoreDir(workspace), DaemonHeartbeat.DefaultKind, Environment.ProcessId);
        if (!File.Exists(beatPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DaemonHeartbeat>(File.ReadAllText(beatPath), SqueezrJson.FileOptions);
        }
        catch (IOException)
        {
            return null;
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
            await Task.Delay(10);
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
        ConfigFile.Save(workspace, ConfigFile.Load(workspace) with
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
