using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Semantic.Embeddings;
using Squeezr.Semantic.Indexing.Watch;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Tests.Indexing.Ignore;
using Squeezr.Tests.Storage;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Workspace;

// The reconcile in one sentence: a copied store holds another branch's numbers, and this is the pass that puts
// them back on this one. Everything here runs against fakes for the provider and the collection, because the
// two facts the contract is made of — which chunks were re-embedded and whether the collection's points cover
// the chunk hashes that claim a vector — have to be counted exactly, not measured against a live service.
[Collection("ProcessGlobals")]
public sealed class WorktreeReconcileTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string LaneBranch = "feat/116-reconcile-a";
    private const string RepoName = "demo";

    private const string KeptSource = """
        public sealed class Kept
        {
            public int Value => 1;
        }
        """;

    private const string TwinSource = """
        public sealed class Twin
        {
            public int Value => 2;
        }
        """;

    private const string ShiftedSource = """
        public sealed class Shifted
        {
            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    // Three lines of prose go above the marker, so the number the lane has to answer with is not the number
    // the main worktree holds: an index that kept the copied coordinates would still say 3.
    private const string ShiftedSourceOnTheBranch = """
        public sealed class Shifted
        {
            // branch note
            // branch note
            // branch note
            public int Add(int a, int b)
            {
                return a + b;
            }
        }
        """;

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public WorktreeReconcileTests()
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

    // The threshold in one number: every row a fill lands already reads 'ok', so a pass that only trusts the
    // database would flip a lane whose collection is empty to fresh. The claim is the obligation, and the
    // collection answers to it.
    [Fact]
    public void Reconcile_CopiedStoreWhoseCollectionIsShortOfItsOkRows_StaysPendingByNumbers()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        var claimed = CountEmbeddedRows(registered.RootPath);
        claimed.ShouldNotBe(0, "the copied rows carry no claim would make this threshold meaningless");

        // The lane's collection lost half its points before this pass ran — a vector arm that died quietly is
        // the state reconcile exists to refuse.
        foreach (var id in collection.Points.Keys.Take(collection.Points.Count / 2).ToList())
        {
            collection.Points.Remove(id);
        }

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        outcome.State.ShouldBe(WorktreeReconcileState.Pending);
        outcome.Code.ShouldBe(WorktreeReconcileOutcome.CollectionShortCode);
        outcome.PassRan.ShouldBeTrue();
        outcome.Obligation.ShouldBeTrue();
        outcome.CollectionPoints.ShouldBe(collection.Points.Count);
        outcome.CoveredChunks.ShouldNotBeNull().ShouldBeLessThan(outcome.ObligationChunks,
            "chunk hashes this store calls embedded have no point among the ids the collection holds — that "
            + "gap is the pending, and it is measured in hashes because a point answers a hash, not a row");
    }

    // The count and the coverage are different facts, and only the second one earns the word reconciled: a
    // collection holding exactly as many points as this store has claims, none of them answering a single
    // claim of it, is the state a cardinality comparison prints as fresh.
    [Fact]
    public void Reconcile_CollectionHoldsTheRightNumberOfForeignPoints_IsPendingNotReconciled()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        var rowsBeforeTheSwap = CountEmbeddedRows(registered.RootPath);
        rowsBeforeTheSwap.ShouldNotBe(0, "an empty claim would let any collection look covered");

        collection.ReplaceWithForeignIdsOf(rowsBeforeTheSwap);

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        outcome.CollectionPoints.ShouldBe(rowsBeforeTheSwap,
            "the collection answers a full set of points, so a count-based gate has nothing to complain about");
        outcome.State.ShouldBe(WorktreeReconcileState.Pending);
        outcome.Code.ShouldBe(WorktreeReconcileOutcome.CollectionShortCode);
        outcome.CoveredChunks.ShouldBe(0, "none of the hashes this store calls embedded is among those ids");
    }

    // Two byte-identical files hold one chunk hash, and one hash owns one point id: an obligation counted in
    // rows could never be met by this tree, and the lane would read as pending at every session start no
    // matter how healthy its collection is.
    [Fact]
    public void Reconcile_TwoFilesSharingOneChunkHash_ObligationCountsTheHashAndTheLaneLandsReconciled()
    {
        var (main, child) = IndexedMainAndLane(out var collection, withTwinFiles: true);
        var (registered, fill) = FillLane(main, child, collection);

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        var claimingRows = outcome.StoredChunks - outcome.PendingChunks;
        claimingRows.ShouldBeGreaterThan(outcome.ObligationChunks,
            "the fixture has to leave rows sharing a hash, or this test measures an ordinary store");
        outcome.CoveredChunks.ShouldBe(outcome.ObligationChunks,
            "every distinct hash the store calls embedded has its point here");
        outcome.State.ShouldBe(WorktreeReconcileState.Reconciled,
            "a duplicate-content tree is not a short collection, and an obligation in rows would make it one");
    }

    // The count that did not answer is not a count of zero: a lane whose store cannot be read is pending with
    // its own code, never a reconciled green line printed over an unreadable collection.
    [Fact]
    public void Reconcile_CollectionDoesNotAnswer_IsPendingSilentAndNeverReconciled()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        var silent = new FakeVectorStore { AnswerThrows = true };

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(silent, new FakeEmbeddingProvider()));

        outcome.State.ShouldBe(WorktreeReconcileState.Pending);
        outcome.Code.ShouldBe(WorktreeReconcileOutcome.StoreSilentCode);
        outcome.CollectionPoints.ShouldBeNull();
        outcome.CoveredChunks.ShouldBeNull();
        outcome.Obligation.ShouldBeTrue();
        // the silent collection is the service-that-is-down shape, and its remedy names that door
        outcome.Reason.ShouldNotBeNull().ShouldContain(RemedyText.ServicesUp);
    }

    // A store whose bytes will not open is a different fact from a collection that will not answer: same
    // null count, different remedy, and a machine-reader must not have to parse prose to tell them apart.
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public void Reconcile_MetaDbWillNotOpen_IsPendingUnreadableAndNamesDropNotRepairTheDoor()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        // a denied read is one unreadable form; the header that will not decode is the other, and the two
        // are different facts to a reader even though the store answers null for both
        var metaDb = SqueezrPaths.MetaDbPath(registered.RootPath);
        File.SetUnixFileMode(metaDb, UnixFileMode.None);
        Should.Throw<UnauthorizedAccessException>(() => File.OpenRead(metaDb).Dispose());

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        outcome.State.ShouldBe(WorktreeReconcileState.Pending);
        outcome.Code.ShouldBe(WorktreeReconcileOutcome.StoreUnreadableCode);
        outcome.CollectionPoints.ShouldBeNull();
        var unreadableReason = outcome.Reason.ShouldNotBeNull();
        unreadableReason.ShouldContain(RemedyText.DropCommand);
        // D8 is a prohibition, so the pin that satisfies it has to be one too: grepping the sentence for the
        // word 'workspace repair' stays green whether the remedy offers repair or refuses it, and refusing it
        // is the half this state was measured into the text for.
        unreadableReason.Contains($"Run '{RemedyText.WorkspaceRepairCommand}'", StringComparison.Ordinal)
            .ShouldBeFalse(
                "workspace repair sweeps registry rows and never opens a meta.db, so it is not the door this "
                + "sentence may send a reader through");
        // the services are not the broken half here, and pointing at them wastes the reader's next step
        unreadableReason.ShouldNotContain(RemedyText.ServicesUp);
    }

    // The unreadable branch's doctrine is that it reports rather than throws, and the denied read pinned
    // above proves only the half of it a chmod can reach: this is the form that comes out of the engine.
    [Fact]
    public void Reconcile_MetaDbMagicStandsAndItsHeaderFieldsDoNot_IsReportedUnreadableNotThrown()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        LiteDbHeaderCorruption.AfterTheMagic(SqueezrPaths.MetaDbPath(registered.RootPath));

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        outcome.State.ShouldBe(WorktreeReconcileState.Pending);
        outcome.Code.ShouldBe(WorktreeReconcileOutcome.StoreUnreadableCode);
        outcome.CollectionPoints.ShouldBeNull();
        var corruptedReason = outcome.Reason.ShouldNotBeNull();
        corruptedReason.ShouldContain(RemedyText.DropCommand);
        // the same prohibition as the chmod form above: the sentence may mention repair to refuse it, but it
        // may not hand the reader the sweep of registry rows as the verb that opens a meta.db.
        corruptedReason.Contains($"Run '{RemedyText.WorkspaceRepairCommand}'", StringComparison.Ordinal)
            .ShouldBeFalse(
                "workspace repair never opens a meta.db, so this remedy must not name it as the door");
    }

    // The loud pass failure is the shape AC7 refuses: the verb says what did not land and which door finishes
    // it, instead of printing numbers from a pass that never happened.
    [Fact]
    public void Reconcile_PassThrewOnTheWayIn_ReportsPassFailedWithItsNumbersAbsentAndTheIndexRemedy()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        // the pass is a standard index over this root, and a root whose manifest went missing is the loud
        // failure it reports rather than throwing at the verb
        File.Delete(SqueezrPaths.ManifestPath(registered.RootPath));

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        outcome.State.ShouldBe(WorktreeReconcileState.Pending);
        outcome.Code.ShouldBe(WorktreeReconcileOutcome.PassFailedCode);
        outcome.PassRan.ShouldBeTrue("the pass was run and it failed loudly — that is what this shape records");
        outcome.RescannedFiles.ShouldBeNull("a pass that threw has no rescan to report");
        // the remedy for a pass that died is the pass itself, and it runs today
        outcome.Reason.ShouldNotBeNull().ShouldContain(RemedyText.IndexCommand);
    }

    // The re-embed number the contract demands, measured against a real tree: only the file this branch
    // changed is embedded again, the untouched file keeps its chunks, and the marker lands on the line the
    // lane's own file holds.
    [Fact]
    public void Reconcile_BranchChangedOneFile_ReEmbedsOnlyThatFileAndAnswersWithTheBranchLineNumbers()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        File.WriteAllText(Path.Combine(child, "src", "Shifted.cs"), ShiftedSourceOnTheBranch);
        var diskLine = Array.IndexOf(
            File.ReadAllLines(Path.Combine(child, "src", "Shifted.cs")),
            "    public int Add(int a, int b)") + 1;
        diskLine.ShouldBe(6, "the assertion compares against the file on disk, not against the parent's index");
        var (registered, fill) = FillLane(main, child, collection);
        var provider = new FakeEmbeddingProvider();

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, provider));

        outcome.PassRan.ShouldBeTrue();
        outcome.Modified.ShouldBe(1, "the branch changed one file, and the parent's rows for it are re-stamped");
        var totalChunks = CountTotalRows(registered.RootPath);
        outcome.Reembedded.ShouldNotBeNull().ShouldBeLessThan(totalChunks,
            "unchanged chunks are not re-embedded: the pass re-did less than the store holds");
        outcome.Reembedded.ShouldBe(provider.EmbeddedTextCount,
            "the reported number is the number of texts the provider was handed");
        outcome.State.ShouldBe(WorktreeReconcileState.Reconciled);

        // the coordinate proof: the chunk that holds the marker sits where the lane's file puts it
        var chunkLines = ListChunks(registered.RootPath)
            .Where(chunk => chunk.Text.Contains("public int Add"))
            .Select(chunk => chunk.StartLine)
            .ToList();
        chunkLines.ShouldNotBeEmpty("the lane indexes its own checkout, so the method has rows here");
        chunkLines.ShouldContain(line => line == diskLine,
            "the row that covers the tree sits exactly where the lane's file puts it — a comparison loose "
            + "enough to accept the parent's number would prove nothing here");

        // the marker file of a linked worktree is not a source file: three files on disk, three rows, no
        // '.git' row that would answer with the main worktree's absolute path.
        File.Exists(Path.Combine(child, ".git")).ShouldBeTrue("a linked worktree carries .git as a file");
        outcome.StoredFiles.ShouldBe(2, "the .git marker never enters the index");
    }

    // The walk is not the only door into this store: a stored path can come back through the per-file route,
    // and a marker that answers there is the same parent coordinate wearing a different coat.
    [Fact]
    public void Reconcile_MarkerAskedThroughThePerFileRoute_LeavesNoRowForTheReadersToAnswerWith()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        var added = new WorkspaceIndexer().IndexFile(child, RepoName, ".git");

        added.ShouldBeNull("the marker is not indexable content, whichever route named it");
        StoredPaths(child).ShouldNotContain(".git",
            "and a route that refuses must not write the row the walk refused either");
    }

    // The tail this rule exists for: a store that was indexed before the marker was excluded still holds the
    // row, and the read route that finds it must take it away rather than serve the main worktree's gitdir.
    [Fact]
    public void Reconcile_LegacyStoreAlreadyHoldingTheMarkerRow_ReadRouteReapsIt()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        SeedLegacyMarkerRow(child);

        var outcome = new WorkspaceIndexer().IndexFile(child, RepoName, ".git");

        outcome.ShouldBeNull();
        StoredPaths(child).ShouldNotContain(".git", "the legacy row does not get to keep answering");
        ListChunks(child).ShouldNotContain(chunk => chunk.Text.Contains("gitdir", StringComparison.Ordinal),
            "and its chunks go with it, because that is what a search would have returned");
    }

    // A loud failure of the vector arm leaves the copied database calling its chunks embedded. There must be
    // no path where such a lane reads as reconciled.
    [Fact]
    public void Reconcile_VectorArmRefusedLoudly_NeverReportsReconciled()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var registered = WorktreeRegistration.Register(child, null);
        var fill = WorktreeStoreFill.Fill(
            registered, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.NotCopied(
                "the vector store did not answer", WorkspaceException.StorageUnavailableCode));
        foreach (var id in collection.Points.Keys.ToList())
        {
            collection.Points.Remove(id);
        }

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        fill.State.ShouldBe(WorktreeFillState.Filled, "the files landed; only the collection refused");
        outcome.State.ShouldBe(WorktreeReconcileState.Pending);
        outcome.Code.ShouldBe(WorktreeReconcileOutcome.CollectionShortCode);
        outcome.CollectionPoints.ShouldBe(0);
    }

    // A pass that ran here is the proof, and the store keeps it: the next session start reads that record,
    // owes no second pass over the same tree, and pays one bounded ask of the ids its own claim names
    // instead of walking the collection. That ask is an attestation, not a measurement — the numbers this
    // verdict reports are the ones the record carries, and 'basis' is the field that says so.
    [Fact]
    public void Reconcile_LaneAlreadyReconciled_SecondRunOwesNoPassAndClaimsTheRecordNotAMeasurement()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        var provider = new FakeEmbeddingProvider();
        var first = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, provider));
        first.State.ShouldBe(WorktreeReconcileState.Reconciled);
        first.PassRan.ShouldBeTrue();
        var callsAfterFirst = provider.EmbedCallCount;
        var askedAfterFirst = collection.AskedIds.Count;
        askedAfterFirst.ShouldBe(first.ObligationChunks,
            "the pass that proved the tree asked its whole claim by id, once — and the walk that used to "
            + "answer the same question is gone from this path");

        var adopted = WorktreeRegistration.Register(child, null);
        var kept = WorktreeStoreFill.Fill(adopted, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.Moved(0, 0, collection.Points.Count));
        kept.State.ShouldBe(WorktreeFillState.Skipped);
        kept.Skip.ShouldBe(WorktreeFillSkip.LaneKeepsItsIndex);
        var askedBefore = collection.AskedIds.Count;

        var second = WorktreeReconcile.Run(
            adopted, kept, noSemantic: false,
            () => Runtime(collection, provider));

        second.PassRan.ShouldBeFalse("no second pass over the same tree at the next session start");
        second.State.ShouldBe(WorktreeReconcileState.Reconciled,
            "the store remembers the pass that proved it, which is what retired the answer that hedged");
        second.Basis.ShouldBe("record");
        second.CoveredChunks.ShouldBeNull("a record-backed verdict measures nothing, so it reports nothing new");
        second.CollectionPoints.ShouldBeNull("and the collection's size is not a number this call asked for");
        second.Record!.CoveredChunks.ShouldBe(second.ObligationChunks,
            "what the record counted is still on the wire — remembered, and labelled as remembered");
        second.ProbedIds.ShouldBe(Math.Min(ReconcileProbeBudget.AttestationSampleIds, second.ObligationChunks),
            "the attestation is a spread of the claim's own ids, not the whole claim and not the collection");
        collection.MeasurementCallCount.ShouldBe(0,
            "the cost clause of review-116-11 §3: no walk of the collection on a healthy session start, and "
            + "none on the pass either — the claim is asked by id");
        collection.AskedIds.Count.ShouldBe(askedBefore + second.ProbedIds!.Value);
        provider.EmbedCallCount.ShouldBe(callsAfterFirst, "a pass that was not owed asked for no embeddings");
        WorkspaceManifestFile.TryRead(registered.RootPath)!.PassCompleted.ShouldBeTrue(
            "the record the verdict reads is the record the pass wrote, on disk, not in this call");
    }

    // The record proves the coordinates the pass rewrote; it does not freeze the tree afterwards. A file the
    // branch deleted is drift, and drift has its own doors — the stale list on 'status' and the per-file
    // verdict on the tools that name files — so the reconcile word stays what it always meant: was this tree
    // ever passed over.
    [Fact]
    public void Reconcile_RecordProvenThenBranchDeletedAFile_DriftSurfacesWhereDriftLives()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        var first = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));
        first.State.ShouldBe(WorktreeReconcileState.Reconciled);

        // the branch deleted a file the main worktree still holds rows for
        File.Delete(Path.Combine(child, "src", "Kept.cs"));
        var adopted = WorktreeRegistration.Register(child, null);
        var kept = WorktreeStoreFill.Fill(adopted, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.Moved(0, 0, collection.Points.Count));
        kept.Skip.ShouldBe(WorktreeFillSkip.LaneKeepsItsIndex);

        var outcome = WorktreeReconcile.Run(
            adopted, kept, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        outcome.PassRan.ShouldBeFalse();
        outcome.State.ShouldBe(WorktreeReconcileState.Reconciled,
            "a pass over this root did run and did prove the rows it rewrote; the deletion is after that");
        StoreRows(registered.RootPath).Files.ShouldBeGreaterThan(1,
            "the deleted file is still a row here, which is the drift the next pass will reap");
        StaleFileDetector.Detect(child).ShouldContain($"{RepoName}/src/Kept.cs",
            "and the drift is named on the door that owns drift rather than hidden behind a reconcile word");
    }


    // The recovery half of ruling C: the record is not only a reporting surface, it is the predicate that
    // makes an interrupted pass retryable. A lane whose store kept its rows and whose record never closed —
    // the state a killed session leaves — owes the pass on the next start, and the start that runs it closes
    // the record so the following one does not pay again.
    [Fact]
    public void Reconcile_StoreKeptItsRowsWithAnOpenRecord_OwesThePassAndClosesTheRecord()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        var provider = new FakeEmbeddingProvider();
        WorktreeReconcile.Run(registered, fill, noSemantic: false, () => Runtime(collection, provider))
            .State.ShouldBe(WorktreeReconcileState.Reconciled);

        // what a killed pass leaves behind: the rows, and a record that never saw the pass finish
        var manifestPath = SqueezrPaths.ManifestPath(registered.RootPath);
        WorkspaceManifestFile.WriteReconcileAt(manifestPath, registered.RootPath,
            ReconcileMarker.Opened().FilledFrom(exactArmLanded: true, code: null));

        var adopted = WorktreeRegistration.Register(child, null);
        var kept = WorktreeStoreFill.Fill(adopted, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.Moved(0, 0, collection.Points.Count));
        kept.Skip.ShouldBe(WorktreeFillSkip.LaneKeepsItsIndex,
            "the fill is not what owes this pass — the store kept its rows, so only the record can");

        var second = WorktreeReconcile.Run(
            adopted, kept, noSemantic: false, () => Runtime(collection, provider));

        second.PassRan.ShouldBeTrue("an open record owes the pass, which is what makes the interruption heal");
        var reopened = WorkspaceManifestFile.Load(registered.RootPath).Reconcile.ShouldNotBeNull();
        reopened.PassCompleted.ShouldBeTrue("and the start that ran it closed the record for the next one");

        var again = WorktreeRegistration.Register(child, null);
        var againFill = WorktreeStoreFill.Fill(again, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.Moved(0, 0, collection.Points.Count));
        againFill.Skip.ShouldBe(WorktreeFillSkip.LaneKeepsItsIndex);
        var third = WorktreeReconcile.Run(
            again, againFill, noSemantic: false, () => Runtime(collection, provider));

        third.PassRan.ShouldBeFalse("once, not every start: the closed record is what the next session reads");
    }

    // The other half of the session-start rule: a lane that kept its index is not a lane that stayed
    // healthy, because the collection can lose its points between sessions. Nothing on disk changed and the
    // fill owes nothing, so the measured gap is the only thing that can owe this lane its pass. If it does
    // not, the degraded lane reads 're-run this verb' at every start forever — and the verb that re-runs
    // never does the work it asks for.
    [Fact]
    public void Reconcile_LaneKeptItsIndexAndItsCollectionDegraded_TheNextSessionOwesThePassAgain()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        var first = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));
        first.State.ShouldBe(WorktreeReconcileState.Reconciled);
        var obligation = first.ObligationChunks;
        obligation.ShouldNotBe(0, "a lane that claims no vector has no collection left to degrade");

        collection.ReplaceWithForeignIdsOf(obligation);

        var adopted = WorktreeRegistration.Register(child, null);
        var kept = WorktreeStoreFill.Fill(adopted, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.Moved(0, 0, collection.Points.Count));
        kept.State.ShouldBe(WorktreeFillState.Skipped);
        kept.Skip.ShouldBe(WorktreeFillSkip.LaneKeepsItsIndex,
            "the store kept its index, so the fill is not what could owe this session a pass");

        var second = WorktreeReconcile.Run(
            adopted, kept, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        second.PassRan.ShouldBeTrue(
            "the hole in the coverage is the trigger, not the copy — the start that finds it owes the pass, "
            + "so 're-run this verb' names a verb that works instead of one that measures and shrugs");
        second.State.ShouldBe(WorktreeReconcileState.Pending);
        second.Code.ShouldBe(WorktreeReconcileOutcome.CollectionShortCode);
        second.ObligationChunks.ShouldBe(obligation);
        second.CollectionPoints.ShouldBe(obligation,
            "the collection answered a full set of points, so a count has nothing to complain about and it is "
            + "the coverage that owes this lane its pass");
        second.CoveredChunks.ShouldBe(0);
    }

    // The window review-116-18 B1 is made of: the pass ran and closed while the collection would not answer,
    // so the record kept the store pending with an arm it never measured. The service comes back and the ids
    // the record names are all in it — nothing here says short any more, and a verb that only reads the
    // record would print 'reconciled' over a flag it refuses to rewrite. Then the warning is a permanent
    // lease and every remedy it names provably fails to clear it. The disagreement owes the pass, and the
    // pass closes the record with a measurement of its own.
    [Fact]
    public void Reconcile_RecordPendingFromASilentPass_CollectionRestoredOwesThePassThatClosesIt()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var (registered, fill) = FillLane(main, child, collection);
        var provider = new FakeEmbeddingProvider();
        // the service that is not in the room: the pass runs, its measurement comes back silent, and the
        // record it writes says so instead of pretending the arm landed
        var silent = new FakeVectorStore { AnswerThrows = true };

        var first = WorktreeReconcile.Run(
            registered, fill, noSemantic: false, () => Runtime(silent, provider));

        first.State.ShouldBe(WorktreeReconcileState.Pending);
        first.Code.ShouldBe(WorktreeReconcileOutcome.StoreSilentCode);
        var leftBehind = WorkspaceManifestFile.Load(registered.RootPath).Reconcile.ShouldNotBeNull();
        leftBehind.PassCompleted.ShouldBeTrue("the pass did run; only its accounting came back silent");
        leftBehind.SemanticArmLanded.ShouldNotBeNull().ShouldBeFalse();
        leftBehind.Pending.ShouldBeTrue();
        leftBehind.ObligationChunks.ShouldNotBe(0,
            "the claim this recovery has to answer for is the store's own, not one this test invented");

        // the service is back, answering for the collection that holds this store's points: the sample the
        // next start pays finds no gap, so nothing measures the pending any more except the record itself
        var adopted = WorktreeRegistration.Register(child, null);
        var kept = WorktreeStoreFill.Fill(adopted, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.Moved(0, 0, collection.Points.Count));
        kept.Skip.ShouldBe(WorktreeFillSkip.LaneKeepsItsIndex,
            "the fill owes nothing here — only the disagreement between the record and the store can");

        var second = WorktreeReconcile.Run(
            adopted, kept, noSemantic: false, () => Runtime(collection, provider));

        second.PassRan.ShouldBeTrue(
            "a record that says pending and a measurement that says reconciled cannot both stand, and the "
            + "record is the half that owes the work — this is what makes 're-run this verb' a remedy");
        second.State.ShouldBe(WorktreeReconcileState.Reconciled);
        second.Basis.ShouldBe("pass", "the recovery answers with a measurement, not with the remembered one");
        second.CoveredChunks.ShouldNotBeNull()
            .ShouldBe(second.ObligationChunks, "every hash the store calls embedded was asked by id and found");

        var closed = WorkspaceManifestFile.Load(registered.RootPath).Reconcile.ShouldNotBeNull();
        closed.Pending.ShouldBeFalse("the store now holds the record the recovery measured");
        closed.SemanticArmLanded.ShouldNotBeNull().ShouldBeTrue();
        closed.Code.ShouldBeNull();
        closed.CoveredChunks.ShouldBe(closed.ObligationChunks,
            "and the numbers it carries are the ones this pass counted, not the frozen silent ones");

        var again = WorktreeRegistration.Register(child, null);
        var againFill = WorktreeStoreFill.Fill(again, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.Moved(0, 0, collection.Points.Count));
        var third = WorktreeReconcile.Run(
            again, againFill, noSemantic: false, () => Runtime(collection, provider));

        third.PassRan.ShouldBeFalse(
            "the recovery is once, not a fresh every-start bill: the closed record settles on its sample");
    }

    // A lane born before its main worktree was indexed has nothing inherited and nothing short. That is a
    // deferred word, not a failure, and it must not leave the lane pending forever.
    [Fact]
    public void Reconcile_FillSkippedBecauseParentHoldsNoRows_DeferredWithoutAMeasurement()
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        WriteSource(main);
        new WorkspaceManager(main).Init("mainws");
        var child = AddWorktree(main, LaneBranch);
        var registered = WorktreeRegistration.Register(child, null);
        var fill = WorktreeStoreFill.Fill(registered, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.Moved(0, 0, 0));
        fill.State.ShouldBe(WorktreeFillState.Skipped);
        fill.Skip.ShouldBe(WorktreeFillSkip.SourceHasNoRows);
        var collection = new FakeVectorStore();

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, new FakeEmbeddingProvider()));

        outcome.State.ShouldBe(WorktreeReconcileState.Deferred);
        outcome.PassRan.ShouldBeFalse();
        collection.MeasurementCallCount.ShouldBe(0,
            "an empty lane has no collection question to ask, so it costs no round trip either");
    }

    [Fact]
    public void Reconcile_ReindexSkip_DeferredAndLeavesTheStoreEmpty()
    {
        var (main, child) = IndexedMainAndLane(out var collection);
        var registered = WorktreeRegistration.Register(child, null);
        var fill = WorktreeStoreFill.Fill(registered, copy: false, ModelKey(main));
        var provider = new FakeEmbeddingProvider();

        var outcome = WorktreeReconcile.Run(
            registered, fill, noSemantic: false,
            () => Runtime(collection, provider));

        outcome.State.ShouldBe(WorktreeReconcileState.Deferred);
        outcome.PassRan.ShouldBeFalse();
        // the door this sentence names fills the lane today; a promise about a pass that never lands does not
        outcome.Reason.ShouldNotBeNull().ShouldContain(RemedyText.IndexCommand);
        provider.EmbedCallCount.ShouldBe(0);
        StoreRows(registered.RootPath).ShouldBe((0, 0),
            "the explicit from-scratch option stays from-scratch: this verb does not index it for the user");
    }

    private (string Main, string Child) IndexedMainAndLane(
        out FakeVectorStore collection, bool withTwinFiles = false)
    {
        var main = Tracked(TestDirs.NewTempDir());
        TestGitRepo.Init(main);
        WriteSource(main, withTwinFiles);
        new WorkspaceManager(main).Init("mainws");
        // The provider is named and its endpoint is dead: the fake runtime is what embeds here, and no test
        // path in this file may open a connection to the services on this machine.
        TestWorkspaceConfig.UseOllamaEmbeddings(main);
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(main);
        new WorkspaceManager(main).AddRepo(".", RepoName);
        collection = new FakeVectorStore();
        new WorkspaceIndexer().Index(
            main,
            vectorPhase: TestVectorPhase.Runtime(
                new FakeEmbeddingProvider(),
                collection,
                new EmbeddingProviderResolution("ollama", null)));
        // the parent index run has to have produced points, or every threshold below measures an empty pair
        collection.UpsertedPointCount.ShouldNotBe(0);
        var child = AddWorktree(main, LaneBranch);
        return (main, child);
    }

    private (WorktreeRegistrationOutcome Registered, WorktreeFillOutcome Fill) FillLane(
        string main, string child, FakeVectorStore collection)
    {
        var registered = WorktreeRegistration.Register(child, null);
        var fill = WorktreeStoreFill.Fill(
            registered, true, ModelKey(main), null,
            _ => WorktreeVectorCopyStats.Moved(collection.Points.Count, collection.Points.Count,
                collection.Points.Count));
        fill.State.ShouldBe(WorktreeFillState.Filled);
        return (registered, fill);
    }

    // The runtime the verb would build for a lane whose config names ollama: the resolution travels with it,
    // because an 'auto' provider has to be resolved before a model key can be computed at all.
    private static WorktreeReconcileRuntime Runtime(IVectorStore store, IEmbeddingProvider provider)
    {
        var runtime = new WatchEmbeddingRuntime(provider, store, new EmbeddingProviderResolution("ollama", null));
        return new(runtime, runtime, null, []);
    }

    private static Func<string> ModelKey(string main) =>
        () => EmbeddingModelKey.From(SemanticConfigFile.Load(main).Embeddings);

    private static void WriteSource(string root, bool withTwinFiles = false)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "Kept.cs"), KeptSource);
        File.WriteAllText(Path.Combine(root, "src", "Shifted.cs"), ShiftedSource);
        TestGitRepo.Add(root, "src/Kept.cs");
        TestGitRepo.Add(root, "src/Shifted.cs");
        if (withTwinFiles)
        {
            // Two paths, byte-identical content: the chunk hashes collide, so the rows that claim a vector
            // outnumber the points any honest collection can hold for them.
            File.WriteAllText(Path.Combine(root, "src", "TwinOne.cs"), TwinSource);
            File.WriteAllText(Path.Combine(root, "src", "TwinTwo.cs"), TwinSource);
            TestGitRepo.Add(root, "src/TwinOne.cs");
            TestGitRepo.Add(root, "src/TwinTwo.cs");
        }

        TestGitRepo.Commit(root, "seed");
    }

    private static string AddWorktree(string main, string branch)
    {
        var child = Path.Combine(main, ".lanes", "lane-a");
        TestGitRepo.AddWorktree(main, child, branch);
        return child;
    }

    private static int CountEmbeddedRows(string root) =>
        ListChunks(root).Count(chunk => chunk.VectorStatus == "ok");

    private static int CountPendingRows(string root) =>
        ListChunks(root).Count(chunk => chunk.VectorStatus != "ok");

    private static int CountTotalRows(string root) => ListChunks(root).Count;

    private static (int Files, int Chunks) StoreRows(string root)
    {
        using var store = OpenStore(root);
        return (store.CountAllFiles(), store.GetSemanticCoverage().TotalChunks);
    }

    private static List<ChunkDocument> ListChunks(string root)
    {
        using var store = OpenStore(root);
        return store.ListFiles(store.FindRepo(RepoName)!.Id)
            .SelectMany(file => store.ListChunksByFile(file.Id))
            .ToList();
    }

    private static IReadOnlyList<string> StoredPaths(string root)
    {
        using var store = OpenStore(root);
        return store.ListFiles(store.FindRepo(RepoName)!.Id).Select(file => file.Path).ToList();
    }

    // The shape a store indexed before the marker rule carries: a row for the '.git' file and a chunk whose
    // text is the main worktree's gitdir. Hand-seeded, because no current pass would ever write it again.
    private static void SeedLegacyMarkerRow(string root)
    {
        using var store = OpenStore(root);
        var repo = store.FindRepo(RepoName)!;
        var file = new FileDocument
        {
            Id = store.GetNextFileId(),
            RepoId = repo.Id,
            Path = ".git",
            Sha256 = "legacy-seed",
            Language = "text",
            Kind = "docs",
            SizeBytes = 48,
            LineCount = 1,
            IndexedAt = DateTime.UtcNow,
        };
        store.UpsertFile(file);
        var nextChunkId = store.ListFiles(repo.Id)
            .SelectMany(existing => store.ListChunksByFile(existing.Id))
            .Select(chunk => chunk.Id)
            .DefaultIfEmpty(0L)
            .Max() + 1;
        store.InsertChunks([new ChunkDocument
        {
            Id = nextChunkId,
            FileId = file.Id,
            RepoId = repo.Id,
            StartLine = 1,
            EndLine = 1,
            Text = "gitdir: /somewhere/above/.git/worktrees/lane-a",
            TokenCount = 6,
            ChunkHash = "legacy-marker-hash",
            VectorStatus = "pending",
        }]);
    }

    private static MetaStore OpenStore(string root) =>
        new(SqueezrPaths.MetaDbPath(root));

    private string Tracked(string dir)
    {
        _createdDirs.Add(dir);
        return dir;
    }

    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        public int Dimensions { get; init; } = 4;

        public string ModelKey => TestEmbeddingKeys.TestModel;

        public int EmbedCallCount { get; private set; }

        public int EmbeddedTextCount { get; private set; }

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct)
        {
            EmbedCallCount++;
            EmbeddedTextCount += texts.Count;
            return Task.FromResult(texts.Select(text =>
            {
                var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
                var vector = new float[Dimensions];
                for (var i = 0; i < Dimensions; i++)
                {
                    vector[i] = bytes[i % bytes.Length] / 255f;
                }

                return vector;
            }).ToArray());
        }
    }

    private sealed class FakeVectorStore : IVectorStore
    {
        public Dictionary<ulong, VectorPoint> Points { get; } = new();

        // One flag for one fact: this collection is not in the room. Both doors a measurement can be asked
        // through refuse, so a reconcile that reaches for the count instead of the ids gets the same answer.
        // Settable rather than init-only because a recovery has to bring the service back between two runs
        // of the same verb, with the points the first run wrote staying where they landed.
        public bool AnswerThrows { get; set; }

        public int MeasurementCallCount { get; private set; }

        // The ids this collection was asked about, in the order it was asked, so a test can tell the walk of
        // every point from one bounded spread of the claim's own ids — the two doors cost differently and the
        // cost clause of review-116-11 §3 is about exactly that difference.
        public List<ulong> AskedIds { get; } = [];

        public int UpsertedPointCount { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
        {
            UpsertedPointCount += points.Count;
            foreach (var point in points)
            {
                Points[point.Id] = point;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorHit>> SearchAsync(
            float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct)
        {
            ThrowIfUnreachable();
            return Task.FromResult((long)Points.Count);
        }

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct)
        {
            MeasurementCallCount++;
            ThrowIfUnreachable();
            return Task.FromResult<IReadOnlyList<ulong>>(Points.Keys.ToList());
        }

        // The door the reconcile pass asks through, answered off the points this fake holds — so a test can
        // count the ids a session start paid for separately from a walk of the whole collection.
        public Task<IReadOnlyList<ulong>> MissingPointIdsAsync(
            IReadOnlyList<ulong> pointIds,
            CancellationToken ct)
        {
            AskedIds.AddRange(pointIds);
            ThrowIfUnreachable();
            return Task.FromResult<IReadOnlyList<ulong>>(pointIds.Where(id => !Points.ContainsKey(id)).ToList());
        }

        public void ReplaceWithForeignIdsOf(int count)
        {
            Points.Clear();
            for (var index = 0; index < count; index++)
            {
                var id = ChunkPointId.FromHash($"hash-of-a-chunk-that-lives-in-another-store-{index}");
                Points[id] = new VectorPoint(
                    id,
                    new float[4],
                    $"hash-of-a-chunk-that-lives-in-another-store-{index}",
                    FileId: 9_999,
                    RepoId: 9_999,
                    StartLine: 1,
                    EndLine: 2,
                    Kind: "code");
            }
        }

        private void ThrowIfUnreachable()
        {
            if (AnswerThrows)
            {
                throw new HttpRequestException("qdrant is down");
            }
        }

        public Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<uint>>([]);

        public Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct)
        {
            foreach (var id in pointIds)
            {
                Points.Remove(id);
            }

            return Task.CompletedTask;
        }

        public Task DeleteCollectionAsync(CancellationToken ct)
        {
            Points.Clear();
            return Task.CompletedTask;
        }
    }
}
