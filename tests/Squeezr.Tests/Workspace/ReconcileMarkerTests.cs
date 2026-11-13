using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Output;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Workspace;

// The record a store keeps about its own reconcile state, read at the two doors that have to agree about it:
// the manifest file and the reporting surfaces. Everything here is file-level on purpose — the claims are
// about what a killed pass leaves behind and what an old manifest must not suddenly start claiming, and
// neither of those needs a process to answer.
[Collection("ProcessGlobals")]
public sealed class ReconcileMarkerTests : IDisposable
{
    private readonly string _storeDir;

    public ReconcileMarkerTests()
    {
        _storeDir = TestDirs.NewTempDir();
        Directory.CreateDirectory(_storeDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storeDir))
        {
            Directory.Delete(_storeDir, recursive: true);
        }
    }

    private string ManifestPath => Path.Combine(_storeDir, SqueezrPaths.StoreManifestFileName);

    // AC4: a manifest written before this rule has no block, and not having one is not pending — the field
    // is additive (035), so the store that never saw a copy must keep answering exactly as it did.
    [Fact]
    public void ManifestWithoutTheField_LoadsAndReportsNotPending()
    {
        File.WriteAllText(ManifestPath, """
            {"name":"oldws","rootPath":"/tmp/oldws","repos":[{"name":"demo","path":"src"}]}
            """);

        var manifest = WorkspaceManifestFile.LoadAt(ManifestPath);
        manifest.Reconcile.ShouldBeNull();
        var record = WorktreeRecord.Of(manifest.Reconcile);
        record.ShouldBeNull();
        ReconcilePendingNotice.Lines(record).ShouldBeEmpty(
            "a workspace.json without the field owes its reader no pending word at all");
        ReconcilePendingNotice.CoordinatesUnproven(record).ShouldBeFalse();
    }

    // AC3's shape at the door: the fill opens the record before a byte moves, so a pass that died with the
    // copy half-done leaves a store that says pending rather than one that says nothing.
    [Fact]
    public void FillOpensTheRecordBeforeBytes_SoAKilledCopyStillReadsPending()
    {
        WorkspaceManifestFile.WriteReconcileAt(ManifestPath, _storeDir, ReconcileMarker.Opened());

        var marker = WorkspaceManifestFile.LoadAt(ManifestPath).Reconcile.ShouldNotBeNull();
        marker.Filled.ShouldBeFalse("nothing has landed yet, and the record was written before it could");
        marker.Pending.ShouldBeTrue();
        var lines = ReconcilePendingNotice.Lines(WorktreeRecord.Of(marker));
        lines.ShouldHaveSingleItem().Contains("never landed", StringComparison.Ordinal).ShouldBeTrue();
    }

    // AC1's core half: copied rows with no closed pass are pending, and the pending is the sentence a reader
    // can act on — it names the verb that runs the pass today.
    [Fact]
    public void FilledRecordWithNoClosedPass_ReadsPendingAndNamesTheDoorThatRunsThePass()
    {
        var marker = ReconcileMarker.Opened().FilledFrom(exactArmLanded: true, null);
        WorkspaceManifestFile.WriteReconcileAt(ManifestPath, _storeDir, marker);

        var read = WorkspaceManifestFile.LoadAt(ManifestPath).Reconcile.ShouldNotBeNull();
        read.Filled.ShouldBeTrue();
        read.PassCompleted.ShouldBeFalse();
        read.Pending.ShouldBeTrue();
        var line = ReconcilePendingNotice.Lines(WorktreeRecord.Of(read)).ShouldHaveSingleItem();
        line.ShouldContain("reconcile pending");
        line.ShouldContain(RemedyText.WorktreeInitCommand);
        line.Contains(RemedyText.WorkspaceRepairCommand, StringComparison.Ordinal).ShouldBeFalse(
            "the sweep of registry rows is not a door on a reconcile pass and must not be pointed at as one");
    }

    // AC2's core half: a closed pass with the vectors accounted for retires the pending word, and a closed
    // pass whose semantic arm did not land does not — the two are different records and read differently.
    [Fact]
    public void ClosedPassWithVectorsAccounted_ClearsPending_WhileAnUnansweredArmKeepsIt()
    {
        var proven = ReconcileMarker.Opened()
            .FilledFrom(exactArmLanded: true, null)
            .ClosedPass(passCompleted: true, semanticArmLanded: true, obligationChunks: 8, coveredChunks: 8,
                code: null);
        proven.Pending.ShouldBeFalse();
        proven.CoordinatesProven.ShouldBeTrue();
        ReconcilePendingNotice.Lines(WorktreeRecord.Of(proven)).ShouldBeEmpty();

        var silent = proven with { SemanticArmLanded = false, Code = "worktree_reconcile_store_silent", CoveredChunks = null };
        silent.Pending.ShouldBeTrue();
        var line = ReconcilePendingNotice.Lines(WorktreeRecord.Of(silent)).ShouldHaveSingleItem();
        line.Contains("never answered", StringComparison.Ordinal).ShouldBeTrue(
            "an accounting this store never took is not an accounting of zero");
    }

    // The exact arm stays a separate answer rather than folding into 'filled': a lane copied from a parent
    // without a trigram.idx holds real rows and permanently lacks exact search, and a reader that is told
    // only 'filled' will go looking for a search that is not there.
    [Fact]
    public void MissingExactArm_IsSaidApartFromTheFilledAnswer_EvenAfterAProvenPass()
    {
        var marker = ReconcileMarker.Opened()
            .FilledFrom(exactArmLanded: false, null)
            .ClosedPass(passCompleted: true, semanticArmLanded: true, obligationChunks: 0, coveredChunks: 0,
                code: null);
        marker.Pending.ShouldBeFalse("the claim it holds is accounted for");
        var lines = ReconcilePendingNotice.Lines(WorktreeRecord.Of(marker));
        lines.ShouldContain(line => line.Contains("no trigram index", StringComparison.Ordinal),
            "and the permanent gap is still said, because 'pending' is not the only word a reader needs");
    }

    // The write door creates the manifest when a minted lane has none yet, and it must not lose a field the
    // copy already wrote — the marker and the repos a lane owns live in one file.
    [Fact]
    public void WritingTheMarker_KeepsTheRepoEntriesTheSeedOwns()
    {
        WorkspaceManifestFile.SaveAt(ManifestPath, new WorkspaceManifest
        {
            Name = "lane",
            RootPath = _storeDir,
            Repos = [new RepoEntry("demo", ".")],
        });

        WorkspaceManifestFile.WriteReconcileAt(ManifestPath, _storeDir, ReconcileMarker.Opened());

        var manifest = WorkspaceManifestFile.LoadAt(ManifestPath);
        manifest.Repos.ShouldHaveSingleItem().Name.ShouldBe("demo");
        manifest.Reconcile.ShouldNotBeNull();
    }

    // A surface that only asks what the store says must not die over the answer: a manifest that will not
    // parse is read as 'no record', the same as a store nobody ever filled.
    [Fact]
    public void UnreadableManifest_ReadsAsNoRecord_WithoutReachingTheReaderAsAnError()
    {
        File.WriteAllText(ManifestPath, "{ not json");

        WorkspaceManifestFile.TryReadAt(ManifestPath).ShouldBeNull();
        ReconcilePendingNotice.Lines(WorktreeRecord.Of(null)).ShouldBeEmpty();
    }

    // The field is camelCase and null-omitting like the rest of the file, so a reader that walks a pre-T7
    // store's keys sees exactly the keys it saw before.
    [Fact]
    public void MarkerSerializesAsAnAdditiveBlock_AndRoundTripsItsThreeAnswers()
    {
        var marker = ReconcileMarker.Opened()
            .FilledFrom(exactArmLanded: true, null)
            .ClosedPass(passCompleted: true, semanticArmLanded: false, obligationChunks: 12, coveredChunks: 9,
                code: "worktree_reconcile_collection_short");
        WorkspaceManifestFile.WriteReconcileAt(ManifestPath, _storeDir, marker);

        var json = JsonDocument.Parse(File.ReadAllText(ManifestPath)).RootElement;
        json.TryGetProperty("reconcile", out var block).ShouldBeTrue();
        block.GetProperty("filled").GetBoolean().ShouldBeTrue();
        block.GetProperty("passCompleted").GetBoolean().ShouldBeTrue();
        block.GetProperty("semanticArmLanded").GetBoolean().ShouldBeFalse();
        block.GetProperty("coveredChunks").GetInt32().ShouldBe(9);

        var read = WorkspaceManifestFile.LoadAt(ManifestPath).Reconcile.ShouldNotBeNull();
        read.ObligationChunks.ShouldBe(12);
        read.Code.ShouldBe("worktree_reconcile_collection_short");

        var plain = new WorkspaceManifest { Name = "x", RootPath = "/x" };
        JsonSerializer.Serialize(plain, SqueezrJson.FileOptions)
            .Contains("reconcile", StringComparison.Ordinal)
            .ShouldBeFalse("a manifest that never filled writes no block at all rather than one saying false");
    }
}
