using Shouldly;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Workspace;

// The seed is the difference between a filled store and a store whose bytes no query leg can reach: the
// copied repo rows join to manifest entries by name, and the entry is what says where the files are. Every
// case here runs against a temp lane root with no registry row and no central store, so nothing but the
// manifest file and the tree are in play.
public sealed class WorktreeManifestSeedTests
{
    private const string RowName = "fixture";

    private readonly string _root;
    private readonly string _laneRoot;
    private readonly string _manifestPath;

    public WorktreeManifestSeedTests()
    {
        _root = TestDirs.NewTempDir();
        _laneRoot = Path.Combine(_root, "lane");
        Directory.CreateDirectory(_laneRoot);
        var laneStore = Directory.CreateDirectory(Path.Combine(_laneRoot, ".squeezr"));
        _manifestPath = Path.Combine(laneStore.FullName, "workspace.json");
    }

    [Fact]
    public void OwnCopiedRows_MissingRow_TakesTheParentsNameAndRebasesItsPathUnderTheLane()
    {
        Save(new WorkspaceManifest { Name = "lane", RootPath = _laneRoot });
        Directory.CreateDirectory(Path.Combine(_laneRoot, "src"));

        var result = WorktreeManifestSeed.OwnCopiedRows(
            _manifestPath, _laneRoot, [new RepoEntry(RowName, "src")], [RowName]);

        result.Owned.ShouldBe([RowName]);
        result.Unowned.ShouldBeEmpty();
        Load().Repos.ShouldHaveSingleItem().Path.ShouldBe("src",
            "a main-worktree-relative path is lane-relative too — one checkout mirrors the other");
    }

    [Fact]
    public void OwnCopiedRows_LaneAlreadyNamesTheRow_KeepsTheLanesPathAndRewritesNothing()
    {
        Save(new WorkspaceManifest
        {
            Name = "lane",
            RootPath = _laneRoot,
            Repos = [new RepoEntry(RowName, "kept/in/the/lane")],
        });
        var before = File.ReadAllText(_manifestPath);

        var result = WorktreeManifestSeed.OwnCopiedRows(
            _manifestPath, _laneRoot, [new RepoEntry(RowName, ".")], [RowName]);

        result.Owned.ShouldBeEmpty();
        File.ReadAllText(_manifestPath).ShouldBe(before,
            "a pass that changes nothing must not rewrite the manifest it just read");
        Load().Repos.ShouldHaveSingleItem().Path.ShouldBe("kept/in/the/lane");
    }

    // Names are compared the way the read legs compare them — case-insensitively — because two entries that
    // differ only in case break every name-keyed dictionary in the query path.
    [Fact]
    public void OwnCopiedRows_LaneNamesTheRowInAnotherCase_KeepsTheLanesEntry()
    {
        Save(new WorkspaceManifest
        {
            Name = "lane",
            RootPath = _laneRoot,
            Repos = [new RepoEntry("Fixture", "."), new RepoEntry("other", "."),],
        });

        var result = WorktreeManifestSeed.OwnCopiedRows(
            _manifestPath, _laneRoot, [new RepoEntry(RowName, ".")], [RowName]);

        result.Owned.ShouldBeEmpty();
        Load().Repos.Count.ShouldBe(2);
    }

    [Fact]
    public void OwnCopiedRows_RowStoredAtAnAbsolutePathOutsideTheLane_RefusesAndWritesNoParentPath()
    {
        var parentRoot = Path.Combine(_root, "main");
        Directory.CreateDirectory(parentRoot);

        var result = WorktreeManifestSeed.OwnCopiedRows(
            _manifestPath, _laneRoot, [new RepoEntry(RowName, parentRoot)], [RowName]);

        result.Owned.ShouldBeEmpty();
        result.Unowned.ShouldHaveSingleItem().ShouldContain("outside this worktree");
        File.Exists(_manifestPath).ShouldBeFalse("a refusal writes no half-seeded manifest");
    }

    [Fact]
    public void OwnCopiedRows_RelativePathClimbsOutOfTheLane_Refuses()
    {
        Save(new WorkspaceManifest { Name = "lane", RootPath = _laneRoot });

        var result = WorktreeManifestSeed.OwnCopiedRows(
            _manifestPath, _laneRoot, [new RepoEntry(RowName, "../sibling")], [RowName]);

        result.Unowned.ShouldHaveSingleItem().ShouldContain("resolves outside this worktree");
    }

    [Fact]
    public void OwnCopiedRows_LaneCheckoutDoesNotHoldTheDirectory_RefusesAndSaysSo()
    {
        Save(new WorkspaceManifest { Name = "lane", RootPath = _laneRoot });

        var result = WorktreeManifestSeed.OwnCopiedRows(
            _manifestPath, _laneRoot, [new RepoEntry(RowName, "not-checked-out")], [RowName]);

        result.Unowned.ShouldHaveSingleItem().ShouldContain("does not hold");
    }

    [Fact]
    public void OwnCopiedRows_CopiedRowHasNoEntryInTheParentManifest_RefusesWithTheRepairRemedy()
    {
        Save(new WorkspaceManifest { Name = "lane", RootPath = _laneRoot });

        var result = WorktreeManifestSeed.OwnCopiedRows(_manifestPath, _laneRoot, [], [RowName]);

        result.Unowned.ShouldHaveSingleItem().ShouldContain("workspace repair");
    }

    [Fact]
    public void OwnCopiedRows_DatabaseHoldsNoRows_WritesNothingToOwn()
    {
        Save(new WorkspaceManifest { Name = "lane", RootPath = _laneRoot });
        var before = File.ReadAllText(_manifestPath);

        var result = WorktreeManifestSeed.OwnCopiedRows(
            _manifestPath, _laneRoot, [new RepoEntry(RowName, ".")], []);

        result.Owned.ShouldBeEmpty();
        result.Unowned.ShouldBeEmpty();
        File.ReadAllText(_manifestPath).ShouldBe(before);
    }

    private void Save(WorkspaceManifest manifest) => WorkspaceManifestFile.SaveAt(_manifestPath, manifest);

    private WorkspaceManifest Load() => WorkspaceManifestFile.LoadAt(_manifestPath);
}
