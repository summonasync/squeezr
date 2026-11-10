using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Storage;

public sealed class WorkspaceStoreCopyTests
{
    private const string DestinationModel = StoreFixture.ModelKey;

    private readonly string _root;

    public WorkspaceStoreCopyTests()
    {
        _root = TestDirs.NewTempDir();
    }

    [Fact]
    public void Copy_SeededSource_ReportsSourceStagedInstalledAndInstallsWhatItMeasured()
    {
        var source = StoreFixture.Seed(SourceStoreDir());
        var request = Request(source.StoreDir, LaneRoot());

        var result = WorkspaceStoreCopy.Copy(request, DestinationModel);

        var database = result.Database;
        database.SourceFiles.ShouldBe(source.Files);
        database.CopiedFiles.ShouldBe(source.Files);
        database.TargetFiles.ShouldBe(source.Files);
        database.SourceChunks.ShouldBe(source.Chunks);
        database.CopiedChunks.ShouldBe(source.Chunks);
        database.TargetChunks.ShouldBe(source.Chunks);
        database.TargetBytes.ShouldBe(database.SourceBytes,
            "the installed database is the byte-for-byte file the staged copy was");
        result.Trigram.Copied.ShouldBeTrue();
        result.Trigram.SourceDocs.ShouldBe(source.TrigramDocs);
        result.Trigram.CopiedDocs.ShouldBe(source.TrigramDocs);
        result.Trigram.TargetDocs.ShouldBe(source.TrigramDocs);
        result.Trigram.TargetBytes.ShouldBe(result.Trigram.SourceBytes);

        StoreFixture.Counts(request.TargetStoreDir).ShouldBe((source.Files, source.Chunks),
            "the installed copy must answer with the rows the source holds, not with the numbers the report claims");
        StoreFixture.TrigramDocs(request.TargetStoreDir).ShouldBe(source.TrigramDocs);
        FilesIn(request.TargetStoreDir).ShouldBe(["meta.db", "trigram.idx", "workspace.json"],
            "a finished pass leaves exactly the files a filled store is made of — no temp, no write-ahead log, " +
            "under any name");
        FilesIn(source.StoreDir).ShouldBe(["meta.db", "trigram.idx"],
            "the source store is read, never rewritten, by a pass that copies out of it");
        result.OwnedRepos.ShouldBe(["fixture"]);
        result.UnownedRepos.ShouldBeEmpty();
    }

    [Fact]
    public void Copy_SeededSource_LaneManifestOwnsTheCopiedRowByNameAndKeepsItRelativeToTheLane()
    {
        var source = StoreFixture.Seed(SourceStoreDir());
        var request = Request(source.StoreDir, LaneRoot());

        WorkspaceStoreCopy.Copy(request, DestinationModel);

        var manifest = WorkspaceManifestFile.LoadAt(request.TargetManifestPath);
        manifest.Repos.Single().Name.ShouldBe("fixture",
            "the name is the join key every read leg uses against the copied rows — renaming it orphans them");
        manifest.Repos.Single().Path.ShouldBe(".");

        using var installed = new MetaStore(
            Path.Combine(request.TargetStoreDir, SqueezrPaths.MetaDbFileName));
        var databaseNames = installed.ListRepos().Select(repo => repo.Name).ToList();
        databaseNames.ShouldBe(["fixture"],
            "the copied database names a repo the lane manifest has to answer for");
        manifest.Repos.Select(entry => entry.Name).ToList().ShouldBe(databaseNames,
            "every repo row the copied database holds must be answered by a manifest entry, or the lane " +
            "holds bytes no query leg can reach");
    }

    [Fact]
    public void Copy_LaneAlreadyOwnsTheRow_KeepsTheLanesEntryAndWritesNothingNew()
    {
        var source = StoreFixture.Seed(SourceStoreDir());
        var laneRoot = LaneRoot();
        var laneStoreDir = LaneStoreDir(laneRoot);
        WorkspaceManifestFile.SaveAt(
            Path.Combine(laneStoreDir, SqueezrPaths.StoreManifestFileName),
            new WorkspaceManifest { Name = "lane", RootPath = laneRoot, Repos = [new RepoEntry("fixture", ".")] });
        var request = Request(source.StoreDir, laneRoot);

        var result = WorkspaceStoreCopy.Copy(request, DestinationModel);

        result.OwnedRepos.ShouldBeEmpty("the lane already answered for this row, so there was nothing to seed");
        WorkspaceManifestFile.LoadAt(request.TargetManifestPath).Repos.Single().Path.ShouldBe(".");
    }

    [Fact]
    public void Copy_ReposPathLiesOutsideTheLane_IsRefusedAndNoParentPathIsWritten()
    {
        var source = StoreFixture.Seed(SourceStoreDir());
        var laneRoot = LaneRoot();
        var laneStoreDir = LaneStoreDir(laneRoot);
        var manifestPath = Path.Combine(laneStoreDir, SqueezrPaths.StoreManifestFileName);
        WorkspaceManifestFile.SaveAt(
            manifestPath,
            new WorkspaceManifest { Name = "lane", RootPath = laneRoot, Repos = [new RepoEntry("keep", ".")] });
        var request = Request(source.StoreDir, laneRoot) with
        {
            SourceRepos = [new RepoEntry("fixture", source.StoreDir)],
        };

        var result = WorkspaceStoreCopy.Copy(request, DestinationModel);

        result.OwnedRepos.ShouldBeEmpty();
        result.UnownedRepos.ShouldHaveSingleItem().ShouldContain("outside this worktree");
        // an absolute main-worktree path in a lane manifest is the cross-tree answer this feature exists to
        // kill, and the refusal is said — it does not overwrite what the lane already owned.
        var written = File.ReadAllText(manifestPath);
        written.ShouldNotContain(source.StoreDir);
        written.ShouldContain("keep");
        StoreFixture.Counts(laneStoreDir).ShouldBe((source.Files, source.Chunks),
            "a refused row still means the bytes landed — the refusal is said, not hidden by a failed copy");
    }

    [Fact]
    public void Copy_ReposMapsToADirectoryTheCheckoutDoesNotHold_IsRefusedAndSaid()
    {
        var source = StoreFixture.Seed(SourceStoreDir());
        var request = Request(source.StoreDir, LaneRoot()) with
        {
            SourceRepos = [new RepoEntry("fixture", "not-in-this-checkout")],
        };

        var result = WorkspaceStoreCopy.Copy(request, DestinationModel);

        result.OwnedRepos.ShouldBeEmpty();
        result.UnownedRepos.ShouldHaveSingleItem().ShouldContain("does not hold");
    }

    [Fact]
    public void Copy_LiveIndexLockOnSource_RefusesAndNamesTheHolderInsteadOfReadingTornBytes()
    {
        var source = StoreFixture.Seed(SourceStoreDir());
        var request = Request(source.StoreDir, LaneRoot());

        using (IndexLock.Acquire(source.StoreDir))
        {
            var ex = Should.Throw<WorkspaceLockedException>(
                () => WorkspaceStoreCopy.Copy(request, DestinationModel));

            ex.Code.ShouldBe(WorkspaceLockedException.LockedCode);
            // the held state this pass refuses is a live owner with an open handle, and the report names it
            ex.Message.ShouldContain($"pid {Environment.ProcessId}");
        }

        File.Exists(Path.Combine(request.TargetStoreDir, SqueezrPaths.MetaDbFileName)).ShouldBeFalse(
            "a refusal under a live lock must not install a store read from bytes that were in flight");
        Directory.Exists(request.TargetStoreDir).ShouldBeFalse(
            "the source lock is taken first, so a refusal does not even leave a new store directory behind");
    }

    [Fact]
    public void Copy_StaleSourceLockFromADeadPid_IsTakenOverOnTheProductsOwnRulesAndCopies()
    {
        var source = StoreFixture.Seed(SourceStoreDir());
        var laneRoot = LaneRoot();
        var deadPid = FindDeadPid();
        File.WriteAllText(Path.Combine(source.StoreDir, SqueezrPaths.LockFileName), deadPid.ToString());
        var messages = new List<string>();
        var request = Request(source.StoreDir, laneRoot);

        var result = WorkspaceStoreCopy.Copy(request, DestinationModel, messages.Add);

        messages.ShouldContain(
            $"stale lock from pid {deadPid} removed",
            "the copy goes through IndexLock, so a dead holder is taken over by the standard rule and said out loud");
        result.Database.TargetChunks.ShouldBe(source.Chunks);
        FilesIn(source.StoreDir).ShouldBe(["meta.db", "trigram.idx"],
            "the lock file the pass took over is gone with it, under its own name");
    }

    [Fact]
    public void Copy_SourceWithoutTrigramIndex_MovesTheDatabaseAndSaysTheIndexWasAbsent()
    {
        var source = StoreFixture.Seed(SourceStoreDir());
        File.Delete(Path.Combine(source.StoreDir, SqueezrPaths.TrigramIndexFileName));
        var request = Request(source.StoreDir, LaneRoot());

        var result = WorkspaceStoreCopy.Copy(request, DestinationModel);

        result.Trigram.Copied.ShouldBeFalse();
        result.Trigram.TargetBytes.ShouldBe(0);
        result.Trigram.TargetDocs.ShouldBe(0);
        result.Database.TargetChunks.ShouldBe(source.Chunks);
        File.Exists(Path.Combine(request.TargetStoreDir, SqueezrPaths.TrigramIndexFileName)).ShouldBeFalse();
        FilesIn(request.TargetStoreDir).ShouldBe(["meta.db", "workspace.json"]);
    }

    // The child store of this feature's own verb starts as a minted empty database; the fill replaces it,
    // so replacing an existing destination has to be the supported shape, not an accident.
    [Fact]
    public void Copy_TargetAlreadyHoldsItsOwnStore_ReplacesItWholeAndKeepsTheReportedCounts()
    {
        var source = StoreFixture.Seed(SourceStoreDir());
        var laneRoot = LaneRoot();
        var laneStoreDir = Path.Combine(laneRoot, ".squeezr");
        StoreFixture.Seed(laneStoreDir, DestinationModel, files: 1, chunksPerFile: 1);
        var request = Request(source.StoreDir, laneRoot);

        var result = WorkspaceStoreCopy.Copy(request, DestinationModel);

        result.Database.SourceChunks.ShouldBe(source.Chunks);
        result.Database.TargetChunks.ShouldBe(source.Chunks);
        StoreFixture.Counts(laneStoreDir).ShouldBe((source.Files, source.Chunks),
            "the destination answers with the source's rows after the swap, not with the rows it minted");
        StoreFixture.TrigramDocs(laneStoreDir).ShouldBe(source.TrigramDocs);
        FilesIn(laneStoreDir).ShouldBe(["meta.db", "trigram.idx", "workspace.json"]);
    }

    [Fact]
    public void Copy_SourceEmbeddedWithAnotherModel_RefusesBeforeTheDestinationIsTouched()
    {
        var source = StoreFixture.Seed(SourceStoreDir(), "ollama/qmdl/1024");
        var request = Request(source.StoreDir, LaneRoot());

        var ex = Should.Throw<WorkspaceException>(() => WorkspaceStoreCopy.Copy(request, DestinationModel));

        ex.Code.ShouldBe(WorkspaceException.WorktreeModelMismatchCode);
        Directory.Exists(request.TargetStoreDir).ShouldBeFalse("the guard runs under the source lock, before any byte moves");
    }

    [Fact]
    public void Copy_SourceHasNoVectorsYet_MovesPendingRowsWithNoModelNamedAtAll()
    {
        var source = StoreFixture.Seed(SourceStoreDir(), modelKey: null);
        var request = Request(source.StoreDir, LaneRoot());

        var result = WorkspaceStoreCopy.Copy(request, targetModelKey: null);

        result.Database.SourceBytes.ShouldBeGreaterThan(0);
        result.Database.TargetChunks.ShouldBe(source.Chunks, "pending rows still move — they just carry no vectors");
    }

    [Fact]
    public void Copy_SourceAndTargetAreTheSameStore_RefusesWithTheSameStoreCode()
    {
        var source = StoreFixture.Seed(SourceStoreDir());
        var request = Request(source.StoreDir, Path.Combine(_root, "main")) with
        {
            TargetStoreDir = source.StoreDir,
            TargetManifestPath = Path.Combine(source.StoreDir, SqueezrPaths.StoreManifestFileName),
        };

        var ex = Should.Throw<WorkspaceException>(() => WorkspaceStoreCopy.Copy(request, DestinationModel));

        ex.Code.ShouldBe(WorkspaceException.WorktreeSameStoreCode,
            "the refusal is a machine-visible fact, not a sentence a reader has to pattern-match");
        StoreFixture.Counts(source.StoreDir).ShouldBe((source.Files, source.Chunks));
    }

    // The identity guard compares the directory each path reaches, not the spelling: a link-shaped
    // destination naming the source's own store dir is the same store, and a weaker comparison lets it
    // through to take a lock this very process already holds — which dies as workspace_locked naming its
    // own pid instead of refusing.
    [LinuxFact]
    public void Copy_DestinationSpelledThroughALinkOfTheSourceTree_IsRecognisedAsTheSameStore()
    {
        var source = StoreFixture.Seed(SourceStoreDir());
        var link = Path.Combine(_root, "link-to-main");
        Directory.CreateSymbolicLink(link, Path.Combine(_root, "main"));
        var request = Request(source.StoreDir, LaneRoot()) with
        {
            TargetStoreDir = Path.Combine(link, ".squeezr"),
            TargetManifestPath = Path.Combine(link, ".squeezr", SqueezrPaths.StoreManifestFileName),
        };

        var ex = Should.Throw<WorkspaceException>(() => WorkspaceStoreCopy.Copy(request, DestinationModel));

        ex.Code.ShouldBe(WorkspaceException.WorktreeSameStoreCode);
        // a link spelling that slipped past the guard would die as workspace_locked naming this very pid
        ex.Message.ShouldNotContain($"pid {Environment.ProcessId}");
    }

    [Fact]
    public void Copy_SourceStoreHasNoDatabase_RefusesNamingTheIndexRemedy()
    {
        var source = SourceStoreDir();
        Directory.CreateDirectory(source);
        var request = Request(source, LaneRoot());

        var ex = Should.Throw<WorkspaceException>(() => WorkspaceStoreCopy.Copy(request, DestinationModel));

        ex.Code.ShouldBe(WorkspaceException.IndexNotBuiltCode);
        ex.Message.ShouldContain("squeezr index");
    }

    private string SourceStoreDir() => Path.Combine(_root, "main", ".squeezr");

    private string LaneRoot()
    {
        var laneRoot = Path.Combine(_root, "lane");
        Directory.CreateDirectory(laneRoot);
        return laneRoot;
    }

    // A lane store that exists before the pass: the manifest is written into it the way the mint writes
    // it, and a test pre-seeding one cannot rely on the copy to create the directory first.
    private static string LaneStoreDir(string laneRoot)
    {
        var storeDir = Path.Combine(laneRoot, ".squeezr");
        Directory.CreateDirectory(storeDir);
        return storeDir;
    }

    private WorkspaceStoreCopyRequest Request(
        string sourceStoreDir,
        string laneRoot,
        IReadOnlyList<RepoEntry>? sourceRepos = null) =>
        new(
            sourceStoreDir,
            Path.Combine(laneRoot, ".squeezr"),
            laneRoot,
            Path.Combine(laneRoot, ".squeezr", SqueezrPaths.StoreManifestFileName),
            sourceRepos ?? [new RepoEntry("fixture", ".")]);

    // The closed file set, not a pattern the product's own constants write: an assertion built from
    // TempSuffix/LogSuffix stays green when a rename moves the leftover to another name.
    private static IReadOnlyList<string> FilesIn(string storeDir) =>
        Directory.Exists(storeDir)
            ? [.. Directory.GetFiles(storeDir).Select(Path.GetFileName).Select(name => name!).Order(StringComparer.Ordinal)]
            : [];

    private static int FindDeadPid()
    {
        for (var pid = 999_000; ; pid++)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
            }
            catch (ArgumentException)
            {
                return pid;
            }
        }
    }
}
