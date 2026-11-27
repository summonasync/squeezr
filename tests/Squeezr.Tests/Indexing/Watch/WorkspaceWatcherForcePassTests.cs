using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Indexing.Watch;

public class WorkspaceWatcherForcePassTests
{
    [Fact]
    public async Task OnEvent_GitignoreChange_SetsForcePass()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");
        new Core.Indexing.WorkspaceIndexer().Index(workspace);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var watcher = new WorkspaceWatcher();
        await watcher.StartAsync(workspace, cts.Token);

        try
        {
            var gitignorePath = Path.Combine(workspace, "repo", ".gitignore");
            watcher.OnEvent(gitignorePath, pathMayBeGone: false);

            var batch = watcher.CollectDueBatch();
            batch.ShouldNotBeNull("force-pass should trigger a batch even with no pending files");
            batch.ShouldBeEmpty("ignore-file events don't add to pending, they just force a pass");
        }
        finally
        {
            await watcher.StopAsync();
        }
    }

    [Fact]
    public async Task OnEvent_SqueezrignoreChange_SetsForcePass()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");
        new Core.Indexing.WorkspaceIndexer().Index(workspace);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var watcher = new WorkspaceWatcher();
        await watcher.StartAsync(workspace, cts.Token);

        try
        {
            var squeezrignorePath = Path.Combine(workspace, "repo", ".squeezrignore");
            watcher.OnEvent(squeezrignorePath, pathMayBeGone: false);

            var batch = watcher.CollectDueBatch();
            batch.ShouldNotBeNull("force-pass should trigger a batch");
            batch.ShouldBeEmpty();
        }
        finally
        {
            await watcher.StopAsync();
        }
    }

    [Fact]
    public async Task OnEvent_RegularFile_DoesNotSetForcePass()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");
        new Core.Indexing.WorkspaceIndexer().Index(workspace);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var watcher = new WorkspaceWatcher();
        await watcher.StartAsync(workspace, cts.Token);

        try
        {
            var regularPath = Path.Combine(workspace, "repo", "a.cs");
            watcher.OnEvent(regularPath, pathMayBeGone: false);

            var batch = watcher.CollectDueBatch();
            batch.ShouldBeNull("regular file events should not force a pass immediately (debounce applies)");
        }
        finally
        {
            await watcher.StopAsync();
        }
    }

    [Theory]
    [InlineData("Squeezr.slnx")]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("Feed.csproj")]
    public async Task OnEvent_ProjectLayoutEdit_QueuesThePathForTheHealPass(string fileName)
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");
        new Core.Indexing.WorkspaceIndexer().Index(workspace);
        var layoutPath = Path.Combine(workspace, "repo", fileName);
        File.WriteAllText(layoutPath, "<Solution />");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var watcher = new WorkspaceWatcher();
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            watcher.OnEvent(layoutPath, pathMayBeGone: false);

            var batch = await NextBatchAsync(watcher);
            batch.ShouldNotBeNull("a project-layout edit has to reach a pass");
            batch.ShouldContain(layoutPath, "the heal trigger reads the layout path out of the batch");
        }
        finally
        {
            await watcher.StopAsync();
        }
    }

    [Fact]
    public async Task OnEvent_EditorAndBuildNoise_QueuesNoPass()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");
        new Core.Indexing.WorkspaceIndexer().Index(workspace);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var watcher = new WorkspaceWatcher();
        await watcher.StartAsync(workspace, cts.Token);
        try
        {
            foreach (var fileName in new[] { "a.cs.tmp", "Squeezr.slnx.bak", "Feed.csproj.user", "notes.unknownext" })
            {
                var noisePath = Path.Combine(workspace, "repo", fileName);
                File.WriteAllText(noisePath, "noise");
                watcher.OnEvent(noisePath, pathMayBeGone: false);
            }

            watcher.CollectDueBatch().ShouldBeNull("none of the noise paths is tracked");
            await Task.Delay(TimeSpan.FromMilliseconds(1200));
            watcher.CollectDueBatch().ShouldBeNull("and the debounce window cannot turn them into a pass");
        }
        finally
        {
            await watcher.StopAsync();
        }
    }

    [Fact]
    public async Task OnEvent_MultipleIgnoreChanges_CollapseIntoOneBatch()
    {
        var workspace = NewWorkspace();
        WriteFile(workspace, "repo/a.cs", "class A { }");
        new Core.Indexing.WorkspaceIndexer().Index(workspace);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var watcher = new WorkspaceWatcher();
        await watcher.StartAsync(workspace, cts.Token);

        try
        {
            var gitignorePath = Path.Combine(workspace, "repo", ".gitignore");
            watcher.OnEvent(gitignorePath, pathMayBeGone: false);
            watcher.OnEvent(gitignorePath, pathMayBeGone: false);
            watcher.OnEvent(gitignorePath, pathMayBeGone: false);

            var batch = watcher.CollectDueBatch();
            batch.ShouldNotBeNull();
            batch.ShouldBeEmpty("multiple ignore-file events should collapse into one force-pass");

            var secondBatch = watcher.CollectDueBatch();
            secondBatch.ShouldBeNull("after consuming the force-pass, no more batches until next event");
        }
        finally
        {
            await watcher.StopAsync();
        }
    }

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

    private static async Task<List<string>?> NextBatchAsync(WorkspaceWatcher watcher)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var batch = watcher.CollectDueBatch();
            if (batch is not null)
            {
                return batch;
            }

            await Task.Delay(100);
        }

        return null;
    }

    private static void UseFastDebounce(string workspace)
    {
        SemanticConfigFile.Save(workspace, SemanticConfigFile.Load(workspace) with
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
}
