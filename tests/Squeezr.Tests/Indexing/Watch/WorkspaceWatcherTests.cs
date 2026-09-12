using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Indexing.Watch;

public class WorkspaceWatcherTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan QuietSettle = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task ModifiedFile_TriggersSinglePass_SearchSeesNewContent()
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
            WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"freshneedle\"; }");

            await UntilAsync(() => recorder.PassesCompleted >= 1);
            recorder.Faults.ShouldBeEmpty();
            await Task.Delay(QuietSettle);
            recorder.PassesStarted.ShouldBe(1);

            new SearchEngine(workspace).Search(Query("freshneedle")).Matches
                .ShouldHaveSingleItem().Path.ShouldBe("a.cs");
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task DeletedFile_TriggersPass_NoGhostsInSearch()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"goneedle\"; }");
        WriteFile(workspace, "repo/b.cs", "class Beta { }");
        new WorkspaceIndexer().Index(workspace);
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            File.Delete(Path.Combine(workspace, "repo", "a.cs"));

            await UntilAsync(() => recorder.PassesCompleted >= 1);
            recorder.Faults.ShouldBeEmpty();

            new SearchEngine(workspace).Search(Query("goneedle")).Matches.ShouldBeEmpty();
            new SearchEngine(workspace).Search(Query("class Beta")).Matches.ShouldHaveSingleItem();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task DeletedDirectory_TriggersPass_NoGhostsForWholeTree()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/tree/a.cs", "class A { string Term = \"dirneedle\"; }");
        WriteFile(workspace, "repo/tree/b.cs", "class B { }");
        WriteFile(workspace, "repo/keep.cs", "class Keep { string Term = \"keepneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            Directory.Delete(Path.Combine(workspace, "repo", "tree"), recursive: true);

            await UntilAsync(() => recorder.PassesCompleted >= 1);
            recorder.Faults.ShouldBeEmpty();

            new SearchEngine(workspace).Search(Query("dirneedle")).Matches.ShouldBeEmpty();
            new SearchEngine(workspace).Search(Query("class B")).Matches.ShouldBeEmpty();
            new SearchEngine(workspace).Search(Query("keepneedle")).Matches.ShouldHaveSingleItem();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task StormOfEdits_CollapsesIntoSinglePass()
    {
        var workspace = NewWorkspace();
        for (var i = 0; i < 25; i++)
        {
            WriteFile(workspace, $"repo/storm/file{i:D2}.cs", $"class Storm{i} {{ string Term = \"old{i}\"; }}");
        }
        new WorkspaceIndexer().Index(workspace);
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            for (var i = 0; i < 25; i++)
            {
                WriteFile(workspace, $"repo/storm/file{i:D2}.cs", $"class Storm{i} {{ string Term = \"stormneedle{i}\"; }}");
            }

            await UntilAsync(() => recorder.PassesCompleted >= 1);
            recorder.Faults.ShouldBeEmpty();
            await Task.Delay(QuietSettle);
            recorder.PassesStarted.ShouldBe(1);

            new SearchEngine(workspace).Search(Query("stormneedle24")).Matches
                .ShouldHaveSingleItem().Path.ShouldBe("storm/file24.cs");
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task NonAsciiPath_ChangeVisibleInSearch()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/データ/файл.cs", "class Alpha { string Term = \"oldneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            WriteFile(workspace, "repo/データ/файл.cs", "class Alpha { string Term = \"unicodeneedle\"; }");

            await UntilAsync(() => recorder.PassesCompleted >= 1);
            recorder.Faults.ShouldBeEmpty();

            new SearchEngine(workspace).Search(Query("unicodeneedle")).Matches
                .ShouldHaveSingleItem().Path.ShouldBe("データ/файл.cs");
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task LockedIndex_RetriesWithBackoff_PicksPassUpAfterLockRelease()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"lockedneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            using (IndexLock.Acquire(SqueezrPaths.StoreDir(workspace)))
            {
                WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"freedneedle\"; }");
                await UntilAsync(() => recorder.Retries >= 1);
                recorder.PassesCompleted.ShouldBe(0);
            }

            await UntilAsync(() => recorder.PassesCompleted >= 1);
            recorder.Faults.ShouldBeEmpty();

            new SearchEngine(workspace).Search(Query("freedneedle")).Matches.ShouldHaveSingleItem();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task WriteInsideSqueezrStore_DoesNotTriggerPass()
    {
        var workspace = NewRootRepoWorkspace();
        WriteFile(workspace, "a.cs", "class Alpha { string Term = \"oldneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            File.WriteAllText(Path.Combine(SqueezrPaths.StoreDir(workspace), "noise.txt"), "store noise");

            await Task.Delay(QuietSettle);
            recorder.PassesStarted.ShouldBe(0);

            WriteFile(workspace, "a.cs", "class Alpha { string Term = \"proboneeneedle\"; }");
            await UntilAsync(() => recorder.PassesCompleted >= 1);
            recorder.Faults.ShouldBeEmpty();

            new SearchEngine(workspace).Search(Query("proboneeneedle")).Matches.ShouldHaveSingleItem();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task CancelledToken_StopsEngineWithoutHanging()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"oldneedle\"; }");
        new WorkspaceIndexer().Index(workspace);
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);

        WriteFile(workspace, "repo/a.cs", "class Alpha { string Term = \"cancelledneedle\"; }");
        await cts.CancelAsync();

        var stop = watcher.StopAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!stop.IsCompleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        stop.IsCompleted.ShouldBeTrue("watcher did not stop within 5s after cancellation");
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

    private static SearchQuery Query(string text) => new(text, false, null, null, null, null, null, null);

    private static string NewWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("repo", "demo");
        UseFastDebounce(workspace);
        return workspace;
    }

    private static string NewRootRepoWorkspace()
    {
        var workspace = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo(".", "demo");
        UseFastDebounce(workspace);
        return workspace;
    }

    private static void UseFastDebounce(string workspace)
    {
        ConfigFile.Save(workspace, ConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { DebounceSeconds = 1 }
        });
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
        private int _started;
        private int _completed;
        private int _retries;

        public int PassesStarted => Volatile.Read(ref _started);

        public int PassesCompleted => Volatile.Read(ref _completed);

        public int Retries => Volatile.Read(ref _retries);

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

        public void PassStarted(int changedPaths) => Interlocked.Increment(ref _started);

        public void PassCompleted(IndexResult result) => Interlocked.Increment(ref _completed);

        public void RetryScheduled(TimeSpan delay) => Interlocked.Increment(ref _retries);

        public void Faulted(string error)
        {
            lock (_gate)
            {
                _faults.Add(error);
            }
        }
    }
}
