using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Ignore;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Indexing.Watch;

public class WorkspaceWatcherTrackedLifecycleTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void OnEvent_GitInternalWrite_ForcesAPassWithoutPendingPaths()
    {
        var watcher = new WorkspaceWatcher();

        watcher.CollectDueBatch().ShouldBeNull();

        watcher.OnEvent("/repo/.git/index", pathMayBeGone: false);

        watcher.CollectDueBatch().ShouldNotBeNull().ShouldBeEmpty();
    }

    [Fact]
    public async Task MidWatchCommit_IndexesTheNewlyTrackedFile_WithoutForeignChurn()
    {
        var workspace = NewPluginsWorkspace();
        IngestionWorkspaces.StoredPaths(workspace).ShouldNotContain("plugins/a.js");

        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            var repoRoot = IngestionWorkspaces.RepoRoot(workspace);
            TestGitRepo.Add(repoRoot, "plugins/a.js");
            TestGitRepo.Commit(repoRoot, "ship plugin");

            await UntilAsync(() => IngestionWorkspaces.StoredPaths(workspace).Contains("plugins/a.js"));
            recorder.Faults.ShouldBeEmpty();

            IngestionWorkspaces.StoredPaths(workspace).ShouldNotContain("plugins/notes.txt");
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public async Task EditAfterMidWatchCommit_ReachesThePassThroughTheRefreshedFilter()
    {
        var workspace = NewPluginsWorkspace();
        var recorder = new WatchRecorder();
        using var cts = new CancellationTokenSource();
        var watcher = new WorkspaceWatcher(recorder);
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            var repoRoot = IngestionWorkspaces.RepoRoot(workspace);
            TestGitRepo.Add(repoRoot, "plugins/a.js");
            TestGitRepo.Commit(repoRoot, "ship plugin");
            await UntilAsync(() => IngestionWorkspaces.StoredPaths(workspace).Contains("plugins/a.js"));

            File.WriteAllText(
                IngestionWorkspaces.Full(workspace, "plugins/a.js"),
                "export function boot() { return \"rebornneedle\"; }\n");

            await UntilAsync(() => Search(workspace, "rebornneedle").Matches.Count == 1);
            recorder.Faults.ShouldBeEmpty();
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    private static SearchResponse Search(string workspace, string text) =>
        new SearchEngine(workspace).Search(new SearchQuery(text, false, null, null, null, null, null, null));

    private static string NewPluginsWorkspace()
    {
        var workspace = IngestionWorkspaces.NewEmptyWorkspace();
        var repoRoot = IngestionWorkspaces.RepoRoot(workspace);
        TestGitRepo.Init(repoRoot);
        Directory.CreateDirectory(Path.Combine(repoRoot, "plugins"));
        File.WriteAllText(Path.Combine(repoRoot, "plugins", "a.js"), "export function boot() { return 1; }\n");
        File.WriteAllText(Path.Combine(repoRoot, "plugins", "notes.txt"), "scratch\n");
        File.WriteAllText(Path.Combine(repoRoot, ".gitignore"), "/plugins\n");
        TestGitRepo.Add(repoRoot, ".gitignore");
        new WorkspaceIndexer().Index(workspace);
        SemanticConfigFile.Save(workspace, SemanticConfigFile.Load(workspace) with
        {
            Watch = new WatchConfig { DebounceSeconds = 1 }
        });
        return workspace;
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

    private sealed class WatchRecorder : IWatchStatus
    {
        private readonly object _gate = new();
        private readonly List<string> _faults = [];

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

        public void PassCompleted(IndexResult result)
        {
        }

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
