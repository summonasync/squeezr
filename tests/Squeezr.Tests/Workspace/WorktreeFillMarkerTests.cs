using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;
using Squeezr.Tests.Storage;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Workspace;

// The record the fill writes, at the door that writes it. Three claims live here and nowhere else: the
// record is opened before a byte moves (so a refused or killed copy cannot leave a store that reads as
// earned), the exact arm is reported by the copy that carried it or did not, and every deliberate no-copy
// keeps its own machine name rather than sharing the word 'Skipped'.
[Collection("ProcessGlobals")]
public sealed class WorktreeFillMarkerTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string LaneBranch = "feat/116-marker-a";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorktreeFillMarkerTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = Tracked(TestDirs.NewTempDir());
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        Directory.CreateDirectory(_home);
        File.WriteAllText(WorkspaceRegistry.FilePath, "[]");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    // AC1's maker: a fill that lands rows leaves the store pending, and it is the record that says so — the
    // database is full and the pass has not run, which is exactly the pair 'filled and pending' means.
    [Fact]
    public void Fill_LandingBytes_WritesFilledWithNoClosedPass_LeavingTheStorePending()
    {
        var main = IndexedMain(StoreFixture.ModelKey, withTrigram: true);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);

        var outcome = WorktreeStoreFill.Fill(registered, true, () => StoreFixture.ModelKey);

        outcome.State.ShouldBe(WorktreeFillState.Filled);
        var marker = WorkspaceManifestFile.Load(registered.RootPath).Reconcile.ShouldNotBeNull();
        marker.Filled.ShouldBeTrue();
        marker.ExactArmLanded.ShouldBe(true, "the parent had a trigram.idx, so the exact arm came over");
        marker.PassCompleted.ShouldBeFalse();
        marker.SemanticArmLanded.ShouldBeNull("the copy cannot answer for the collection; the pass does");
        marker.Pending.ShouldBeTrue();
    }

    // The crash-honest clause in the shape that is easiest to produce on purpose: a refusal inside the copy
    // path leaves no rows AND a record that already said it was open. A store that holds nothing has to be
    // readable as unfinished, never as finished-and-unproven.
    [Fact]
    public void Fill_RefusedByTheModelGuard_LeavesNoRowsButARecordThatStillSaysPending()
    {
        var main = IndexedMain(StoreFixture.ModelKey, withTrigram: true);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var childStoreDir = StoreDirOf(registered.StoreKey!);
        File.WriteAllText(
            Path.Combine(childStoreDir, SqueezrPaths.ConfigFileName),
            System.Text.Json.JsonSerializer.Serialize(
                Squeezr.Semantic.Configuration.SemanticConfig.Bind(Squeezr.Core.Configuration.SqueezrConfig.CreateDefault()) with
                {
                    Embeddings = new Squeezr.Semantic.Configuration.EmbeddingsConfig
                    {
                        Provider = "ollama",
                        Ollama = new Squeezr.Semantic.Configuration.OllamaOptions
                        {
                            Model = "other-model",
                            ModelProfile = null,
                            Endpoint = TestWorkspaceConfig.DeadOllamaEndpoint,
                        },
                    },
                },
                Squeezr.Core.Serialization.SqueezrJson.FileOptions));

        var ex = Should.Throw<WorkspaceException>(
            () => WorktreeStoreFill.Fill(registered, true, () => "ollama/lane-model/768"));

        ex.Code.ShouldBe(WorkspaceException.WorktreeModelMismatchCode);
        StoreFixture.Counts(childStoreDir).ShouldBe((0, 0), "the refusal is before the copy");
        WorkspaceManifestFile.Load(registered.RootPath).Reconcile.ShouldNotBeNull().Pending
            .ShouldBeTrue("and the record opened before the refusal stands, so nothing here reads as earned");
    }

    // review-116-08 §2 caveat 1, pinned where the fact is knowable: a parent whose store holds rows but no
    // trigram.idx yields a child that keeps meta rows and permanently lacks exact search. The content of
    // meta.db alone would call that store filled and complete; this field is what keeps the two apart.
    [Fact]
    public void Fill_FromAParentWithoutATrigramIndex_RecordsTheExactArmAsMissing()
    {
        var main = IndexedMain(modelKey: null, withTrigram: false);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);

        var outcome = WorktreeStoreFill.Fill(registered, true, () => StoreFixture.ModelKey);

        outcome.State.ShouldBe(WorktreeFillState.Filled);
        outcome.Copy.ShouldNotBeNull().Trigram.Copied.ShouldBeFalse();
        var marker = WorkspaceManifestFile.Load(registered.RootPath).Reconcile.ShouldNotBeNull();
        marker.Filled.ShouldBeTrue();
        marker.ExactArmLanded.ShouldBe(false, "the copy says which arm it did not bring");
        StoreFixture.Counts(StoreDirOf(registered.StoreKey!)).ShouldBe((3, 6),
            "and the rows really did land, which is why 'filled' alone would have been the wrong warning");
    }

    // Finding N-4: every deliberate no-copy keeps its own machine name, because a reader that branches on
    // 'Skipped' cannot tell a lane born before its parent was indexed from a lane whose parent lost its
    // store file. The causes are produced by the states rather than read off the enum, and the premises the
    // states depend on are asserted inside each one.
    [Fact]
    public void Fill_EveryNoCopyItCanReach_CarriesItsOwnMachineReason()
    {
        var reindex = SkipOf(ParentWithRows("why-reindex"), "why-reindex", copy: false);
        var nothingMinted = SkipOf(ParentWithoutAWorkspaceRow("why-nothing"), "why-nothing");
        var noStore = SkipOf(ParentWithRows("why-nostore", dropStoreFile: true), "why-nostore");
        var emptyStore = SkipOf(ParentWithEmptyStore("why-empty"), "why-empty");
        var noManifest = SkipOf(ParentWithRows("why-nomanifest", dropManifest: true), "why-nomanifest");
        var noModel = SkipOf(ParentWithRows("why-nomodel", modelKey: StoreFixture.ModelKey), "why-nomodel",
            resolverThrows: true);
        var kept = SkipOf(ParentWithRows("why-kept"), "why-kept", twice: true);

        reindex.ShouldBe(WorktreeFillSkip.ReindexRequested);
        nothingMinted.ShouldBe(WorktreeFillSkip.NothingMinted);
        noStore.ShouldBe(WorktreeFillSkip.SourceStoreMissing);
        emptyStore.ShouldBe(WorktreeFillSkip.SourceHasNoRows);
        noManifest.ShouldBe(WorktreeFillSkip.SourceManifestMissing);
        noModel.ShouldBe(WorktreeFillSkip.ModelUnresolved);
        kept.ShouldBe(WorktreeFillSkip.LaneKeepsItsIndex);
    }

    // The record a fill opens survives a lane store whose manifest was never seeded, because a minted lane
    // has no workspace.json until the copy writes one: the marker door creates the file it needs rather than
    // refusing on a path a fresh mint has not reached yet.
    [Fact]
    public void Fill_OverAMintedStoreWithNoManifest_CreatesTheRecordWithoutLosingTheRow()
    {
        var main = IndexedMain(StoreFixture.ModelKey, withTrigram: true);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var manifestPath = SqueezrPaths.ManifestPath(registered.RootPath);
        if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        File.Exists(manifestPath).ShouldBeFalse(
            "the premise is measured, not assumed: this lane store has no manifest to update");

        WorktreeStoreFill.Fill(registered, true, () => StoreFixture.ModelKey)
            .State.ShouldBe(WorktreeFillState.Filled);

        var manifest = WorkspaceManifestFile.Load(registered.RootPath);
        manifest.Repos.ShouldHaveSingleItem().Name.ShouldBe(StoreFixture.RepoName);
        manifest.Reconcile.ShouldNotBeNull().Filled.ShouldBeTrue();
    }

    private WorktreeFillSkip SkipOf(
        string main,
        string name,
        bool copy = true,
        bool resolverThrows = false,
        bool twice = false)
    {
        var child = AddWorktree(main, $"feat/116-{name}");
        var registered = WorktreeRegistration.Register(child, null);
        Func<string> resolver = resolverThrows
            ? () => throw new InvalidOperationException("no provider in the room")
            : () => StoreFixture.ModelKey;
        if (twice)
        {
            WorktreeStoreFill.Fill(registered, true, resolver).State.ShouldBe(WorktreeFillState.Filled);
            registered = WorktreeRegistration.Register(child, null);
        }

        var outcome = WorktreeStoreFill.Fill(registered, copy, resolver);
        outcome.State.ShouldBe(WorktreeFillState.Skipped, outcome.Reason);
        return outcome.Skip.ShouldNotBeNull();
    }

    // A main worktree the verb can be filled from: registered, one repo, rows in the database under that
    // same name. The knobs take away exactly one fact each, so every skip below names a different hole.
    private string ParentWithRows(
        string name,
        bool dropStoreFile = false,
        bool dropManifest = false,
        string? modelKey = null)
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        new WorkspaceManager(main).Init($"main-{name}");
        new WorkspaceManager(main).AddRepo(".", StoreFixture.RepoName);
        StoreFixture.Seed(StoreDirOf(StoreKeyOfRoot(main)), modelKey);
        File.Exists(SqueezrPaths.MetaDbPath(main)).ShouldBeTrue();
        if (dropStoreFile)
        {
            File.Delete(SqueezrPaths.MetaDbPath(main));
            File.Exists(SqueezrPaths.MetaDbPath(main)).ShouldBeFalse("the premise: no store file behind the row");
        }

        if (dropManifest)
        {
            File.Delete(SqueezrPaths.ManifestPath(main));
            WorkspaceManifestFile.Exists(main).ShouldBeFalse("the premise: a row and a store, no manifest");
        }

        return main;
    }

    // Registered and empty: a store that opens, answers zero files and zero repos. The lane born into this
    // has nothing to inherit and no failure to report.
    private string ParentWithEmptyStore(string name)
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        new WorkspaceManager(main).Init($"main-{name}");
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(main), createIfMissing: true);
        store.CountAllFiles().ShouldBe(0);
        store.ListRepos().Count.ShouldBe(0);
        return main;
    }

    private string ParentWithoutAWorkspaceRow(string name)
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
        return main;
    }

    private string IndexedMain(string? modelKey, bool withTrigram)
    {
        var main = ParentWithRows($"indexed-{Guid.NewGuid().ToString("N")[..8]}", modelKey: modelKey);
        if (!withTrigram)
        {
            var trigram = Path.Combine(StoreDirOf(StoreKeyOfRoot(main)), SqueezrPaths.TrigramIndexFileName);
            File.Exists(trigram).ShouldBeTrue("the premise: the seed brought an exact arm to take away");
            File.Delete(trigram);
            File.Exists(trigram).ShouldBeFalse();
        }

        return main;
    }

    private static string AddWorktree(string main, string branch)
    {
        var child = Path.Combine(main, ".lanes", branch.Split('/')[^1]);
        TestGitRepo.AddWorktree(main, child, branch);
        return child;
    }

    private string StoreDirOf(string storeKey) => WorkspaceStorePaths.RequireStoreDir(storeKey);

    private string StoreKeyOfRoot(string root)
    {
        var canonical = WorkspaceCanonicalPath.Canonicalize(root);
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(WorkspaceRegistry.FilePath));
        return doc.RootElement.EnumerateArray()
            .Single(row => row.GetProperty("rootPath").GetString() == canonical)
            .GetProperty("storeKey").GetString()!;
    }

    private string Tracked(string dir)
    {
        _createdDirs.Add(dir);
        return dir;
    }
}
