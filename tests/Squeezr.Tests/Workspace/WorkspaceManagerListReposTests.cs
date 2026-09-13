using Shouldly;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Workspace;

public sealed class WorkspaceManagerListReposTests
{
    [Fact]
    public void ListRepos_ReturnsManifestReposInOrder()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "services", "billing"));
        Directory.CreateDirectory(Path.Combine(root, "services", "users"));
        var manager = new WorkspaceManager(root);
        manager.Init("my-system");
        manager.AddRepo("services/billing", "billing");
        manager.AddRepo("services/users", "users");

        var repos = manager.ListRepos();

        repos.Count.ShouldBe(2);
        repos[0].ShouldBe(new RepoEntry("billing", "services/billing"));
        repos[1].ShouldBe(new RepoEntry("users", "services/users"));
    }

    [Fact]
    public void ListRepos_OnFreshWorkspace_ReturnsEmpty()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init(null);

        manager.ListRepos().ShouldBeEmpty();
    }

    [Fact]
    public void ListRepos_Throws_WhenNotInitialized()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);

        var ex = Should.Throw<WorkspaceException>(() => manager.ListRepos());
        ex.Code.ShouldBe(WorkspaceException.NotInitializedCode);
    }
}
