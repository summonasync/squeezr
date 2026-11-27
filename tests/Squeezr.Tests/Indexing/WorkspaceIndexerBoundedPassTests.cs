using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Indexing;

public class WorkspaceIndexerBoundedPassTests
{
    [Fact]
    public void Index_CanceledToken_AbortsInsideLockScope_AndReleasesStoreLock()
    {
        var workspace = NewWorkspace();
        File.WriteAllText(Path.Combine(workspace, "a.cs"), "class A { }");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(
            () => new WorkspaceIndexer().Index(workspace, cancellationToken: cts.Token));

        // the whole point of the bound: the abort must not leave index.lock behind
        using var probe = IndexLock.Acquire(SqueezrPaths.StoreDir(workspace));
        probe.ShouldNotBeNull();
    }

    [Fact]
    public void Index_LiveToken_CompletesAndReleasesStoreLock()
    {
        var workspace = NewWorkspace();
        File.WriteAllText(Path.Combine(workspace, "a.cs"), "class A { string Term = \"bounded\"; }");
        using var cts = new CancellationTokenSource();

        var result = new WorkspaceIndexer().Index(workspace, cancellationToken: cts.Token);

        result.Repos.ShouldHaveSingleItem();
        using var probe = IndexLock.Acquire(SqueezrPaths.StoreDir(workspace));
        probe.ShouldNotBeNull();
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
}
