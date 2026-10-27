using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;

namespace Squeezr.Tests.Indexing.Watch;

public class WorkspaceWatcherLinkedWorktreeTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task MidWatchCommit_InLinkedWorktree_IndexesTheNewlyTrackedFile()
    {
        var workspace = NewLinkedWorktreeWorkspace();
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
        }
        finally
        {
            await StopWatcherAsync(watcher, cts);
        }
    }

    [Fact]
    public void GitDirLink_ResolvesTheRealGitdirOfALinkedWorktree()
    {
        var main = TestDirs.NewTempDir();
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        var workspace = TestDirs.NewTempDir();
        var repoRoot = Path.Combine(workspace, "repo");
        TestGitRepo.AddWorktree(main, repoRoot, "linked");
        File.Exists(Path.Combine(repoRoot, ".git")).ShouldBeTrue("a linked worktree carries .git as a file, not a directory");

        var gitdir = GitDirLink.Resolve(repoRoot);

        gitdir.ShouldNotBeNull();
        gitdir.ShouldBe(Path.Combine(main, ".git", "worktrees", "repo"));
        Directory.Exists(gitdir).ShouldBeTrue();
    }

    [Fact]
    public void GitDirLink_PlainRepo_ResolvesToNull()
    {
        var plain = TestDirs.NewTempDir();
        TestGitRepo.Init(plain);
        GitDirLink.Resolve(plain).ShouldBeNull("a normal repo keeps git state inside .git, nothing to link");
    }

    [Fact]
    public void GitDirLink_DanglingPointer_ResolvesToNull()
    {
        var husk = TestDirs.NewTempDir();
        var repoRoot = Path.Combine(husk, "repo");
        Directory.CreateDirectory(repoRoot);
        File.WriteAllText(Path.Combine(repoRoot, ".git"), "gitdir: /nonexistent/worktree\n");
        GitDirLink.Resolve(repoRoot).ShouldBeNull("a dangling pointer degrades to no link");
    }

    private static string NewLinkedWorktreeWorkspace()
    {
        var fixtureRoot = TestDirs.NewTempDir();
        var main = Path.Combine(fixtureRoot, "main");
        var workspace = Path.Combine(fixtureRoot, "workspace");
        Directory.CreateDirectory(main);
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        File.WriteAllText(Path.Combine(main, ".gitignore"), "/plugins\n");
        TestGitRepo.Add(main, "seed.txt", ".gitignore");
        TestGitRepo.Commit(main, "seed");
        Directory.CreateDirectory(workspace);
        var repoRoot = Path.Combine(workspace, "repo");
        TestGitRepo.AddWorktree(main, repoRoot, "linked");
        Directory.CreateDirectory(Path.Combine(repoRoot, "plugins"));
        File.WriteAllText(Path.Combine(repoRoot, "plugins", "a.js"), "export function boot() { return 1; }\n");
        var manager = new WorkspaceManager(workspace);
        manager.Init(null);
        manager.AddRepo("repo", "demo");
        new WorkspaceIndexer().Index(workspace);
        ConfigFile.Save(workspace, ConfigFile.Load(workspace) with
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
