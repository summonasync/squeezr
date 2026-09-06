using Shouldly;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Workspace;

public class WorkspaceManagerStatusTests
{
    [Fact]
    public void Status_ListsAddedReposWithZeroStats()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "services", "billing"));
        Directory.CreateDirectory(Path.Combine(root, "services", "users"));
        var manager = new WorkspaceManager(root);
        manager.Init("my-system");
        manager.AddRepo("services/billing", "billing");
        manager.AddRepo("services/users", "users");

        var status = manager.GetStatus();

        status.Name.ShouldBe("my-system");
        status.Repos.Count.ShouldBe(2);
        status.Repos[0].ShouldBe(new RepoStatus("billing", "services/billing", 0, 0, 0, null));
        status.Repos[1].ShouldBe(new RepoStatus("users", "services/users", 0, 0, 0, null));
    }

    [Fact]
    public void Status_OnFreshWorkspace_HasNoRepos()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init(null);

        var status = manager.GetStatus();

        status.Repos.ShouldBeEmpty();
    }

    [Fact]
    public void Status_Throws_WhenNotInitialized()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);

        Should.Throw<WorkspaceException>(() => manager.GetStatus());
    }
}
