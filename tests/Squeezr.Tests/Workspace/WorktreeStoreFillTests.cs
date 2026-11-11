using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;
using Squeezr.Tests.Storage;

namespace Squeezr.Tests.Workspace;

[Collection("ProcessGlobals")]
public sealed class WorktreeStoreFillTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string LaneBranch = "feat/116-fill-a";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorktreeStoreFillTests()
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
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Fill_MintedChild_CopiesTheParentsRowsIntoTheRowThisVerbResolved()
    {
        var main = IndexedMainWithContent(StoreFixture.ModelKey);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var storeDirsBefore = StoreDirNames();
        var calls = 0;

        var outcome = WorktreeStoreFill.Fill(registered, true, () =>
        {
            calls++;
            return StoreFixture.ModelKey;
        });

        outcome.State.ShouldBe(WorktreeFillState.Filled);
        var copy = outcome.Copy.ShouldNotBeNull();
        copy.Database.SourceFiles.ShouldBe(3);
        copy.Database.TargetFiles.ShouldBe(3);
        copy.Database.SourceChunks.ShouldBe(6);
        copy.Database.TargetChunks.ShouldBe(6);
        copy.Database.TargetBytes.ShouldBe(copy.Database.SourceBytes);
        copy.Trigram.Copied.ShouldBeTrue();
        copy.Trigram.SourceDocs.ShouldBe(3);
        copy.Trigram.TargetDocs.ShouldBe(3);
        calls.ShouldBe(1, "the parent holds vectors, so the model question is asked exactly once");

        var childStoreDir = StoreDirOf(registered.StoreKey!);
        StoreFixture.Counts(childStoreDir).ShouldBe((3, 6),
            "the copy lands where the row the verb reported says it lands");
        StoreFixture.TrigramDocs(childStoreDir).ShouldBe(3);
        StoreDirNames().ShouldBe(storeDirsBefore, "a fill writes into the child store it was reached through");
        Directory.GetDirectories(SqueezrHome.WorkspacesDir).Length.ShouldBe(2,
            "one main store and one lane store — a fill must never mint a third");
    }

    // The bytes are only half of a store: without a manifest entry naming them, every read leg drops the
    // row and the filled lane answers nothing. This is the assertion review-116-06 measured missing.
    [Fact]
    public void Fill_MintedChild_LaneManifestOwnsTheCopiedRowAndNoParentPathCrosses()
    {
        var main = IndexedMainWithContent(StoreFixture.ModelKey);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);

        var outcome = WorktreeStoreFill.Fill(registered, true, () => StoreFixture.ModelKey);

        var copy = outcome.Copy.ShouldNotBeNull();
        copy.OwnedRepos.ShouldBe([StoreFixture.RepoName]);
        copy.UnownedRepos.ShouldBeEmpty();
        var manifest = WorkspaceManifestFile.Load(registered.RootPath);
        manifest.Repos.ShouldHaveSingleItem().Name.ShouldBe(StoreFixture.RepoName,
            "the name the copied rows already carry is the name the manifest has to answer with");
        manifest.Repos.ShouldHaveSingleItem().Path.ShouldBe(".");
        // the lane root sits under the main root by construction, so the invariant is about the entries: a
        // repos path that is absolute is a main-worktree coordinate the lane would answer with
        manifest.Repos.ShouldAllBe(entry => !Path.IsPathRooted(entry.Path));
    }

    // A parent that was registered and never given a repo holds nothing to inherit, and a store left empty
    // by that fact must not be re-copied at every session start — the skip is the honest answer, and it
    // costs no provider probe.
    [Fact]
    public void Fill_ParentStoreHoldsNoRows_SkipsNamesTheParentAndNeverAsksForTheModelKey()
    {
        var main = RegisteredMain(repoAdded: false);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var asked = false;

        var outcome = WorktreeStoreFill.Fill(registered, true, () =>
        {
            asked = true;
            return StoreFixture.ModelKey;
        });

        outcome.State.ShouldBe(WorktreeFillState.Skipped);
        asked.ShouldBeFalse("an empty parent cannot disagree with any model");
        outcome.Reason.ShouldNotBeNull().ShouldContain("no indexed rows");
        StoreFixture.Counts(StoreDirOf(registered.StoreKey!)).ShouldBe((0, 0));
    }

    [Fact]
    public void Fill_ParentRowsArePendingOnly_FillsAndNeverAsksForTheModelKey()
    {
        var main = IndexedMainWithContent(modelKey: null);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var asked = false;

        var outcome = WorktreeStoreFill.Fill(registered, true, () =>
        {
            asked = true;
            return StoreFixture.ModelKey;
        });

        asked.ShouldBeFalse("pending rows carry no vectors to inherit an incompatibility from");
        outcome.State.ShouldBe(WorktreeFillState.Filled);
        var copy = outcome.Copy.ShouldNotBeNull();
        copy.Database.TargetChunks.ShouldBe(6, "pending rows still move — they just carry no vectors");
        copy.Database.TargetFiles.ShouldBe(3);
        copy.Trigram.Copied.ShouldBeTrue();
    }

    [Fact]
    public void Fill_ModelKeyMismatch_RefusesBeforeAnyByteOfTheParentReachesTheChild()
    {
        var main = IndexedMainWithContent("ollama/qmdl/1024");
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var childStoreDir = StoreDirOf(registered.StoreKey!);

        var ex = Should.Throw<WorkspaceException>(
            () => WorktreeStoreFill.Fill(registered, true, () => StoreFixture.ModelKey));

        ex.Code.ShouldBe(WorkspaceException.WorktreeModelMismatchCode);
        ex.Message.ShouldContain("--reindex");
        StoreFixture.Counts(childStoreDir).ShouldBe((0, 0),
            "the refusal happens before the copy starts, so the child still holds the empty store init minted");
        File.Exists(Path.Combine(childStoreDir, SqueezrPaths.TrigramIndexFileName)).ShouldBeFalse();
        Directory.GetFiles(childStoreDir).Where(name => name.Contains(".copy-tmp")).ShouldBeEmpty();
    }

    // The provider probe is a round trip that fails on the ordinary session-start path. It must read as a
    // skip the caller can see, not as a thrown command — and the store it leaves behind must still be
    // fillable by the next pass, because this verb is what a hook reruns forever.
    [Fact]
    public void Fill_ModelKeyCannotBeNamed_SkipsVisiblyAndALaterPassCompletesTheFill()
    {
        var main = IndexedMainWithContent(StoreFixture.ModelKey);
        var child = AddWorktree(main, LaneBranch);
        var minted = WorktreeRegistration.Register(child, null);
        var childStoreDir = StoreDirOf(minted.StoreKey!);

        var failed = WorktreeStoreFill.Fill(minted, true,
            () => throw new WorkspaceException("Ollama probe failed (HTTP 503). Set embeddings.provider explicitly."));

        failed.State.ShouldBe(WorktreeFillState.Skipped);
        failed.Reason.ShouldNotBeNull().ShouldContain("could not be named");
        failed.Reason.ShouldContain("later run");
        StoreFixture.Counts(childStoreDir).ShouldBe((0, 0), "a skip that moved no bytes leaves an empty store");

        var adopted = WorktreeRegistration.Register(child, null);
        adopted.State.ShouldBe(WorktreeRegistrationState.AlreadyRegistered);

        var retry = WorktreeStoreFill.Fill(adopted, true, () => StoreFixture.ModelKey);

        retry.State.ShouldBe(WorktreeFillState.Filled,
            "an adopted store whose database is still empty is unfinished work, not a store that keeps its index");
        StoreFixture.Counts(childStoreDir).ShouldBe((3, 6));

        var third = WorktreeStoreFill.Fill(WorktreeRegistration.Register(child, null), true,
            () => throw new InvalidOperationException("a filled store never reaches the provider"));

        third.State.ShouldBe(WorktreeFillState.Skipped,
            "once the store holds content the fill is over — the session-start path stops asking");
    }

    // review-116-04 #1: the tree is claimed by two keys at once. Filling either one would answer with a
    // store the other row's agents never read, so the fork is reported instead of being resolved.
    [LinuxFact]
    public void Fill_TreeClaimedByTwoStoreKeys_RefusesToFillEitherAndMovesNoBytes()
    {
        var baseDir = Tracked(TestDirs.NewTempDir());
        var real = Path.Combine(baseDir, "real");
        Directory.CreateDirectory(real);
        var main = Path.Combine(real, "main");
        Directory.CreateDirectory(main);
        TestGitRepo.Init(main);
        Seed(main);
        RegisterMain(main);
        SeedStoreContent(main, StoreFixture.ModelKey);
        var link = Path.Combine(baseDir, "link");
        Directory.CreateSymbolicLink(link, real);
        var child = Path.Combine(main, ".lanes", "lane-a");
        TestGitRepo.AddWorktree(main, child, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var spelledRoot = Path.Combine(link, "main", ".lanes", "lane-a");
        WorkspaceRegistry.Add(new WorkspaceRegistryEntry(
            "forked",
            WorkspaceCanonicalPath.Canonicalize(spelledRoot),
            WorkspaceStoreKeys.Create("forked", WorkspaceCanonicalPath.Canonicalize(spelledRoot)),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch));

        var ex = Should.Throw<WorkspaceException>(
            () => WorktreeStoreFill.Fill(registered, true, () => StoreFixture.ModelKey));

        ex.Code.ShouldBe(WorkspaceException.WorktreeStoreForkCode);
        ex.Message.ShouldContain("workspace repair");
        ex.Message.ShouldContain("nothing was copied");
        StoreFixture.Counts(StoreDirOf(registered.StoreKey!)).ShouldBe((0, 0));
    }

    // A tree that resolves to no row at all is a different fact from a tree two rows both claim, and only
    // the second one is a fork — T13 documents the codes, so they may not blur.
    [Fact]
    public void Fill_TreeResolvesToNoRow_ReportsTheUnresolvedCodeAndNotAFork()
    {
        var main = IndexedMainWithContent(StoreFixture.ModelKey);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        WorkspaceRegistry.RemoveByRoot(registered.RootPath);

        var ex = Should.Throw<WorkspaceException>(
            () => WorktreeStoreFill.Fill(registered, true, () => StoreFixture.ModelKey));

        ex.Code.ShouldBe(WorkspaceException.WorktreeStoreUnresolvedCode);
        // a tree with no row is not a tree with two rows, and only the second is a fork
        ex.Message.ShouldNotContain("store keys");
        StoreFixture.Counts(StoreDirOf(registered.StoreKey!)).ShouldBe((0, 0));
    }

    [Fact]
    public void Fill_ReindexMode_SkipsAndNamesThePassThatDoesNotExistYet()
    {
        var main = IndexedMainWithContent(StoreFixture.ModelKey);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);

        var outcome = WorktreeStoreFill.Fill(registered, false, () => throw new InvalidOperationException("probed"));

        outcome.State.ShouldBe(WorktreeFillState.Skipped);
        outcome.Copy.ShouldBeNull();
        outcome.Reason.ShouldNotBeNull().ShouldContain("--reindex");
        outcome.Reason.ShouldContain("reconcile pass");
        // review-116-06 #3: text may only promise a command that runs today, and no pass runs in the lane
        // until the reconcile pass lands — so the sentence must not point at 'squeezr index'.
        outcome.Reason.ShouldNotContain("squeezr index");
        StoreFixture.Counts(StoreDirOf(registered.StoreKey!)).ShouldBe((0, 0));
    }

    // The verb runs at every session start; a store that already holds content must not be re-copied over.
    [Fact]
    public void Fill_AlreadyFilledStore_SkipsAndPointsAtTheDropThatRefills()
    {
        var main = IndexedMainWithContent(StoreFixture.ModelKey);
        var child = AddWorktree(main, LaneBranch);
        var minted = WorktreeRegistration.Register(child, null);
        WorktreeStoreFill.Fill(minted, true, () => StoreFixture.ModelKey).State.ShouldBe(WorktreeFillState.Filled);
        var adopted = WorktreeRegistration.Register(child, null);
        adopted.State.ShouldBe(WorktreeRegistrationState.AlreadyRegistered);

        var outcome = WorktreeStoreFill.Fill(adopted, true, () => throw new InvalidOperationException("probed"));

        outcome.State.ShouldBe(WorktreeFillState.Skipped);
        outcome.Reason.ShouldNotBeNull().ShouldContain("already registered");
        outcome.Reason.ShouldContain($"squeezr drop --store-key '{adopted.StoreKey}'");
        StoreFixture.Counts(StoreDirOf(adopted.StoreKey!)).ShouldBe((3, 6),
            "the first fill's work survives the second pass untouched");
    }

    // The vector arm gets the rows this pass resolved, not names or paths it recomputes: the collection a
    // copy writes into has to be the collection the lane's own serve path reads afterwards. And it is asked
    // after the files landed — the semantic arm is the second half of one fill, not a separate pass that can
    // be reached over an empty store.
    [Fact]
    public void Fill_SourceHoldsEmbeddedChunks_HandsTheArmTheResolvedRowsAfterTheFilesLanded()
    {
        var main = IndexedMainWithContent(StoreFixture.ModelKey);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var childStoreDir = StoreDirOf(registered.StoreKey!);
        WorktreeVectorCopyRequest? seen = null;

        var outcome = WorktreeStoreFill.Fill(registered, true, () => StoreFixture.ModelKey, null, request =>
        {
            StoreFixture.Counts(childStoreDir).ShouldBe((3, 6),
                "the database is already in place when the vector arm is asked");
            // row counts alone do not say the rows are embedded, and the arm inherits its coverage reading
            // from them: a copied chunk still marked pending here would be a lane the copy was asked to
            // finish with its own points, and the gate that decides whether to ask reads this number
            using var copied = new MetaStore(Path.Combine(childStoreDir, SqueezrPaths.MetaDbFileName));
            copied.GetSemanticCoverage().PendingChunks.ShouldBe(0,
                "'ok' names the model that produced the vector, and the arm must not be handed a lane whose " +
                "rows still wait for one");
            seen = request;
            return WorktreeVectorCopyStats.Moved(6, 6, 6);
        });

        var arm = seen.ShouldNotBeNull();
        arm.SourceRoot.ShouldBe(WorkspaceRegistry.FindByAnyRootSpelling(main)!.RootPath);
        arm.TargetRoot.ShouldBe(registered.RootPath);
        arm.TargetStoreKey.ShouldBe(registered.StoreKey);
        outcome.Vectors.ShouldNotBeNull().CopiedPoints.ShouldBe(6);
    }

    // A parent whose rows are all pending owns no points, so the fill must not open a conversation with a
    // vector store on its behalf — every CLI fixture in this lane starts from that shape, and a pass that
    // reached Qdrant anyway would write collections under a developer's own endpoint.
    [Fact]
    public void Fill_ParentRowsArePendingOnly_VectorArmIsNeverAsked()
    {
        var main = IndexedMainWithContent(modelKey: null);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);

        var outcome = WorktreeStoreFill.Fill(registered, true, () => StoreFixture.ModelKey, null,
            _ => throw new InvalidOperationException("the vector arm was asked for a parent with no vectors"));

        outcome.State.ShouldBe(WorktreeFillState.Filled);
        outcome.Copy.ShouldNotBeNull().Database.TargetChunks.ShouldBe(6);
        outcome.Vectors.ShouldBeNull();
    }

    [Fact]
    public void Fill_ModelMismatch_ThrowsBeforeTheVectorArmIsAsked()
    {
        var main = IndexedMainWithContent("ollama/qmdl/1024");
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var asked = false;

        var ex = Should.Throw<WorkspaceException>(() => WorktreeStoreFill.Fill(
            registered, true, () => StoreFixture.ModelKey, null,
            _ =>
            {
                asked = true;
                return WorktreeVectorCopyStats.Moved(0, 0, 0);
            }));

        ex.Code.ShouldBe(WorkspaceException.WorktreeModelMismatchCode);
        asked.ShouldBeFalse("no point moves on a pass the model check refused");
    }

    // A vector store that is not in the room is a session-start fact, not a failed session: the files landed,
    // the fill is Filled, and the arm's reason rides the report so the reader can see the lane is short.
    [Fact]
    public void Fill_VectorStoreRefused_FillStandsAndTheArmReportsItsOwnState()
    {
        var main = IndexedMainWithContent(StoreFixture.ModelKey);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);

        var outcome = WorktreeStoreFill.Fill(registered, true, () => StoreFixture.ModelKey, null,
            _ => WorktreeVectorCopyStats.NotCopied("the vector store at 'http://localhost:1' did not answer"));

        outcome.State.ShouldBe(WorktreeFillState.Filled);
        var vectors = outcome.Vectors.ShouldNotBeNull();
        vectors.Copied.ShouldBeFalse();
        vectors.Reason.ShouldNotBeNull().ShouldContain("did not answer");
        StoreFixture.Counts(StoreDirOf(registered.StoreKey!)).ShouldBe((3, 6));
    }

    // Three numbers that disagree are a failed command. The refusal does not rewind the file arms: undoing a
    // verified copy is not this arm's call, and the half-filled store stays visible to the pass that can
    // finish it.
    [Fact]
    public void Fill_VectorCopyRefusesLoudly_CommandFailsAndTheCopiedFilesStayWhereTheyAre()
    {
        var main = IndexedMainWithContent(StoreFixture.ModelKey);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var childStoreDir = StoreDirOf(registered.StoreKey!);

        var ex = Should.Throw<WorkspaceException>(() => WorktreeStoreFill.Fill(
            registered, true, () => StoreFixture.ModelKey, null,
            _ => throw new WorkspaceException(
                "held 6 point(s), wrote 6, counts 3 after the writes",
                WorkspaceException.WorktreeVectorMismatchCode)));

        ex.Code.ShouldBe(WorkspaceException.WorktreeVectorMismatchCode);
        StoreFixture.Counts(childStoreDir).ShouldBe((3, 6),
            "the refusal is about the collection, so the database it inherits its chunk rows from stays put");
        File.Exists(Path.Combine(childStoreDir, SqueezrPaths.TrigramIndexFileName)).ShouldBeTrue();
    }

    [Fact]
    public void Fill_VectorArmRan_ModelQuestionWasAskedOnce()
    {
        var main = IndexedMainWithContent(StoreFixture.ModelKey);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var asked = 0;

        WorktreeStoreFill.Fill(registered, true, () =>
        {
            asked++;
            return StoreFixture.ModelKey;
        }, null, _ => WorktreeVectorCopyStats.Moved(6, 6, 6));

        asked.ShouldBe(1,
            "the copy carries vectors it never re-derives: a second model question would mean a provider " +
            "round trip inside the pass that is only allowed to move points");
    }

    // The verb runs at every session start. A lane that already got its fill must not be scrolled again —
    // a second copy over the same collection is the same ids written twice, and a second collection would be
    // the identity fork this whole feature closes.
    [Fact]
    public void Fill_LaneAlreadyFilledAndVectorized_VectorArmIsNotAskedAgain()
    {
        var main = IndexedMainWithContent(StoreFixture.ModelKey);
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        WorktreeStoreFill.Fill(registered, true, () => StoreFixture.ModelKey, null,
            _ => WorktreeVectorCopyStats.Moved(6, 6, 6)).State.ShouldBe(WorktreeFillState.Filled);

        var adopted = WorktreeRegistration.Register(child, null);
        var asked = false;
        var outcome = WorktreeStoreFill.Fill(adopted, true, () => StoreFixture.ModelKey, null,
            _ =>
            {
                asked = true;
                return WorktreeVectorCopyStats.Moved(6, 6, 6);
            });

        outcome.State.ShouldBe(WorktreeFillState.Skipped);
        outcome.Vectors.ShouldBeNull();
        asked.ShouldBeFalse();
    }

    private string IndexedMainWithContent(string? modelKey)
    {
        var main = RegisteredMain(repoAdded: true);
        SeedStoreContent(main, modelKey);
        return main;
    }

    private string RegisteredMain(bool repoAdded)
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        Seed(main);
        new WorkspaceManager(main).Init("mainws");
        if (repoAdded)
        {
            AddMainRepo(main);
        }

        return main;
    }

    // The manifest entry and the repo row are one fact in a real store, and 'squeezr add' writes the pair
    // together. A parent whose manifest does not name its own rows is the broken state the copy is meant to
    // refuse, not the shape every test should start from.
    private static void AddMainRepo(string main) => new WorkspaceManager(main).AddRepo(".", StoreFixture.RepoName);

    private static void RegisterMain(string main)
    {
        new WorkspaceManager(main).Init("mainws");
        AddMainRepo(main);
    }

    private void SeedStoreContent(string main, string? modelKey) =>
        StoreFixture.Seed(StoreDirOf(WorkspaceRegistry.FindByRootPath(main)!.StoreKey), modelKey);

    private static void Seed(string main)
    {
        File.WriteAllText(Path.Combine(main, "seed.txt"), "seed\n");
        TestGitRepo.Add(main, "seed.txt");
        TestGitRepo.Commit(main, "seed");
    }

    private static string AddWorktree(string main, string branch)
    {
        var child = Path.Combine(main, ".lanes", "lane-a");
        TestGitRepo.AddWorktree(main, child, branch);
        return child;
    }

    private static string StoreDirOf(string storeKey) => WorkspaceStorePaths.RequireStoreDir(storeKey);

    private string StoreDirNames() =>
        Directory.Exists(SqueezrHome.WorkspacesDir)
            ? string.Join(',', Directory.GetDirectories(SqueezrHome.WorkspacesDir)
                .Select(dir => new DirectoryInfo(dir).Name).Order(StringComparer.Ordinal))
            : string.Empty;

    private string Tracked(string dir)
    {
        _createdDirs.Add(dir);
        return dir;
    }
}
