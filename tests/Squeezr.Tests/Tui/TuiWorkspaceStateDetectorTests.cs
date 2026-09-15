using Shouldly;
using Squeezr.Cli.Tui;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Tui;

public sealed class TuiWorkspaceStateDetectorTests
{
    [Fact]
    public void Detect_UninitializedDirectory_ReturnsUninitialized()
    {
        var dir = TestDirs.NewTempDir();

        TuiWorkspaceStateDetector.Detect(dir).ShouldBe(TuiWorkspaceState.Uninitialized);
    }

    [Fact]
    public void Detect_NestedSubdirectoryOfWorkspace_ResolvesThroughParent()
    {
        var root = TestDirs.NewTempDir();
        new WorkspaceManager(root).Init("demo");
        var nested = Directory.CreateDirectory(Path.Combine(root, "sub", "deep")).FullName;

        TuiWorkspaceStateDetector.Detect(nested).ShouldBe(TuiWorkspaceState.NoRepositories);
    }

    [Fact]
    public void Detect_InitializedWorkspaceWithRepos_ReturnsReady()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init("demo");
        Directory.CreateDirectory(Path.Combine(root, "repo"));
        manager.AddRepo("repo", "repo");

        TuiWorkspaceStateDetector.Detect(root).ShouldBe(TuiWorkspaceState.Ready);
    }
}
