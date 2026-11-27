using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Indexing.Watch;

public class WorkspaceWatcherPacingTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan QuietSettle = TimeSpan.FromSeconds(3);

    [Fact]
    public async Task SecondPass_ParsesOnlyTouchedFile_AfterColdPassWarmedTheSession()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "a.cs", "class Alpha { string Term = \"paceold1\"; }");
        WriteFile(workspace, "b.cs", "class Bravo { string Term = \"paceold2\"; }");
        WriteFile(workspace, "c.cs", "class Charlie { string Term = \"paceold3\"; }");
        new WorkspaceIndexer().Index(workspace);

        var recorder = new PacingRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder, recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            WriteFile(workspace, "a.cs", "class Alpha { string Term = \"paceneedle1\"; }");
            await UntilAsync(() => recorder.PassesCompleted >= 1);
            recorder.SemanticTicks.ShouldHaveSingleItem().ShouldBe(
                ["a.cs", "b.cs", "c.cs"], "the first pass after start has no warm session and parses the repo");

            WriteFile(workspace, "b.cs", "class Bravo { string Term = \"paceneedle2\"; }");
            await UntilAsync(() => recorder.PassesCompleted >= 2);
            await Task.Delay(QuietSettle);
            recorder.PassesStarted.ShouldBe(2, "watcher should not keep indexing after the batch drains");
            recorder.SemanticTicks.Count.ShouldBe(2);
            recorder.SemanticTicks[1].ShouldContain("b.cs");
            recorder.SemanticTicks[1].ShouldNotContain("c.cs", "an untouched file must not be re-parsed by a paced pass");
            recorder.Faults.ShouldBeEmpty();

            var engine = new SearchEngine(workspace);
            engine.Search(Query("paceneedle1")).Matches.ShouldHaveSingleItem().Path.ShouldBe("a.cs");
            engine.Search(Query("paceneedle2")).Matches.ShouldHaveSingleItem().Path.ShouldBe("b.cs");
            engine.Search(Query("paceold2")).Matches.ShouldBeEmpty();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    private static SearchQuery Query(string text) => new(text, false, null, null, null, null, null, null);

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
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo(".", "demo");
        SemanticConfigFile.Save(workspace, SemanticConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { DebounceSeconds = 1 }
        });
        return workspace;
    }

    private static void WriteFile(string workspace, string fileName, string content) =>
        File.WriteAllText(Path.Combine(workspace, fileName), content);

    private sealed class PacingRecorder : IWatchStatus, IIndexProgress
    {
        private readonly object _gate = new();
        private readonly List<string> _faults = [];
        private readonly List<List<string>> _semanticTicks = [];
        private List<string>? _currentPass;
        private IndexPhase _phase;
        private int _passesStarted;
        private int _passesCompleted;

        public int PassesStarted => Volatile.Read(ref _passesStarted);

        public int PassesCompleted => Volatile.Read(ref _passesCompleted);

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

        public IReadOnlyList<IReadOnlyList<string>> SemanticTicks
        {
            get
            {
                lock (_gate)
                {
                    return _semanticTicks.Select(ticks => (IReadOnlyList<string>)ticks.Order().ToList()).ToList();
                }
            }
        }

        public void PassStarted(int changedPaths)
        {
            Interlocked.Increment(ref _passesStarted);
            lock (_gate)
            {
                _currentPass = [];
                _semanticTicks.Add(_currentPass);
            }
        }

        public void PassCompleted(IndexResult result) => Interlocked.Increment(ref _passesCompleted);

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

        public void PhaseStarted(string repoName, IndexPhase phase, int totalItems)
        {
            lock (_gate)
            {
                _phase = phase;
            }
        }

        public void ItemProcessed(string repoName, string currentItem, int? symbolsInserted = null)
        {
            lock (_gate)
            {
                if (_phase == IndexPhase.Semantic)
                {
                    _currentPass?.Add(currentItem);
                }
            }
        }

        public void RepoCompleted(string repoName)
        {
        }
    }
}
