using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Workspace;

public class WorkspaceManagerAddTests
{
    [Fact]
    public void Add_AppendsRepoToManifestAndStore()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "services", "billing"));
        var manager = new WorkspaceManager(root);
        manager.Init("my-system");

        var entry = manager.AddRepo("services/billing", "billing");

        entry.ShouldBe(new RepoEntry("billing", "services/billing"));

        var manifest = WorkspaceManifestFile.Load(root);
        manifest.Repos.ShouldBe([new RepoEntry("billing", "services/billing")]);

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(root));
        var stored = store.ListRepos();
        stored.Count.ShouldBe(1);
        stored[0].Name.ShouldBe("billing");
        stored[0].Path.ShouldBe("services/billing");
    }

    [Fact]
    public void Add_WithoutName_UsesDirectoryName()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "services", "users"));
        var manager = new WorkspaceManager(root);
        manager.Init(null);

        var entry = manager.AddRepo("services/users", null);

        entry.Name.ShouldBe("users");
    }

    [Fact]
    public void Add_AcceptsAbsolutePathInsideWorkspace()
    {
        var root = TestDirs.NewTempDir();
        var repoDir = Path.Combine(root, "gateway");
        Directory.CreateDirectory(repoDir);
        var manager = new WorkspaceManager(root);
        manager.Init(null);

        var entry = manager.AddRepo(repoDir, "gateway");

        entry.Path.ShouldBe("gateway");
    }

    [Fact]
    public void Add_StoresAbsolutePathForOutsideDirectory()
    {
        var root = TestDirs.NewTempDir();
        var outside = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init(null);

        var entry = manager.AddRepo(outside, "external");

        entry.Path.ShouldBe(Path.GetFullPath(outside).Replace('\\', '/'));
    }

    [Fact]
    public void Add_Throws_WhenPathMissing()
    {
        var root = TestDirs.NewTempDir();
        var manager = new WorkspaceManager(root);
        manager.Init(null);

        Should.Throw<WorkspaceException>(() => manager.AddRepo("no/such/dir", "ghost"));
    }

    [Fact]
    public void Add_Throws_OnDuplicateName()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "a"));
        Directory.CreateDirectory(Path.Combine(root, "b"));
        var manager = new WorkspaceManager(root);
        manager.Init(null);
        manager.AddRepo("a", "same");

        Should.Throw<WorkspaceException>(() => manager.AddRepo("b", "same"));
    }

    [Fact]
    public void Add_Throws_OnDuplicatePath()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "a"));
        var manager = new WorkspaceManager(root);
        manager.Init(null);
        manager.AddRepo("a", "first");

        var ex = Should.Throw<WorkspaceException>(() => manager.AddRepo("a", "second"));
        ex.Message.ShouldContain("already");
    }

    [Fact]
    public void Add_Throws_WhenNotInitialized()
    {
        var root = TestDirs.NewTempDir();
        Directory.CreateDirectory(Path.Combine(root, "a"));
        var manager = new WorkspaceManager(root);

        Should.Throw<WorkspaceException>(() => manager.AddRepo("a", "a"));
    }

    [Fact]
    public void Add_CaseDifferentPaths_AreDistinctRepos_OnCaseSensitiveFs()
    {
        var root = TestDirs.NewTempDir();
        var upper = Path.Combine(root, "Repo");
        var lower = Path.Combine(root, "repo");
        Directory.CreateDirectory(upper);
        File.WriteAllText(Path.Combine(upper, "marker.txt"), "");
        if (File.Exists(Path.Combine(lower, "marker.txt")))
        {
            return;
        }
        Directory.CreateDirectory(lower);

        var manager = new WorkspaceManager(root);
        manager.Init(null);
        manager.AddRepo(upper, "upper");

        var entry = manager.AddRepo(lower, "lower");

        entry.Path.ShouldBe("repo");
        WorkspaceManifestFile.Load(root).Repos.Count.ShouldBe(2);
    }
}
