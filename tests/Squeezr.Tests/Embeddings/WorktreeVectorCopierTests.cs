using Grpc.Core;
using Grpc.Net.Client;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Embeddings;

// The semantic arm measured against a qdrant that keeps what it was given. The fake cannot be told a number:
// source, copied and target come off three separate reads, so a run that moved nothing reports three zeroes
// instead of a success, and a batch the server acknowledged and then lost shows up as the disagreement it is.
public class WorktreeVectorCopierTests
{
    private const string SourceRoot = "/workspaces/main";
    private const string ChildRoot = "/workspaces/main/.lanes/lane-a";
    private const string TargetStoreKey = "lane-a-0123456789ab";

    private static readonly string SourceCollection = QdrantVectorStore.GetCollectionName(SourceRoot);
    private static readonly string ChildCollection = QdrantVectorStore.GetCollectionName(ChildRoot);

    [Fact]
    public async Task CopyAsync_SixSourcePoints_LandsSixWholePointsAndReportsTheTripleAsEqual()
    {
        var seeded = SourcePoints(6);
        var fixture = NewFixture(seeded);

        var stats = await CopyAsync(fixture);

        stats.Copied.ShouldBeTrue();
        stats.SourcePoints.ShouldBe(6);
        stats.CopiedPoints.ShouldBe(6);
        // the target count is read back off the collection after the writes, not off the scroll buffer
        stats.TargetPoints.ShouldBe(6);
        var copied = fixture.Fake.Points(ChildCollection);
        copied.Select(point => point.Payload["chunkHash"].StringValue)
            .ShouldBe(seeded.Select(point => point.Payload["chunkHash"].StringValue));
        copied.Select(point => point.Payload.TryGetValue("kind", out var kind) ? kind.StringValue : null)
            .ShouldBe(seeded.Select(point => point.Payload.TryGetValue("kind", out var kind) ? kind.StringValue : null));
        copied.Select(point => point.Vector.Length).ShouldBe([2, 2, 2, 2, 2, 2]);
        copied.Single(point => point.Payload["chunkHash"].StringValue == "hash-4").Vector.ShouldBe([0.4f, 0.8f]);
        copied.Select(point => point.Payload["fileId"].IntegerValue)
            .ShouldBe(seeded.Select(point => point.Payload["fileId"].IntegerValue));
        copied.Select(point => point.Payload["repoId"].IntegerValue)
            .ShouldBe(seeded.Select(point => point.Payload["repoId"].IntegerValue));
        copied.Select(point => point.Payload["startLine"].IntegerValue)
            .ShouldBe(seeded.Select(point => point.Payload["startLine"].IntegerValue));
        copied.Select(point => point.Payload["endLine"].IntegerValue)
            .ShouldBe(seeded.Select(point => point.Payload["endLine"].IntegerValue));
    }

    // What T6 builds on: the id that landed is the id this product computes from the chunk hash, so the
    // reconcile pass can upsert and delete by the id it would have minted itself.
    [Fact]
    public async Task CopyAsync_IdsAreTheOnesTheChunkHashesName_AndNotRekeyedCopies()
    {
        var fixture = NewFixture(SourcePoints(3));

        await CopyAsync(fixture);

        var copied = fixture.Fake.Points(ChildCollection);
        copied
            .Select(point => QdrantVectorStore.PointIdFromChunkHash(point.Payload["chunkHash"].StringValue))
            .ShouldBe(copied.Select(point => point.Id),
                "a copy that renumbered points would strand every later write that names them by content");
    }

    [Fact]
    public async Task CopyAsync_ChildCollectionIsNamedForTheChildRootAndTheParentKeepsWhatItHeld()
    {
        var fixture = NewFixture(SourcePoints(3));

        await CopyAsync(fixture);

        fixture.Target.Collection.ShouldBe(ChildCollection);
        ChildCollection.ShouldNotBe(SourceCollection);
        fixture.Fake.Holds(ChildCollection).ShouldBeTrue();
        fixture.Fake.Points(SourceCollection).Count.ShouldBe(3,
            "the arm reads the main worktree's collection and never writes into it");
    }

    [Fact]
    public async Task CopyAsync_SourceHoldsMoreThanOnePage_MovesEveryPointAcrossThePageAndBatchBounds()
    {
        var fixture = NewFixture(SourcePoints(501));

        var stats = await CopyAsync(fixture);

        fixture.Fake.ScrollCallCount.ShouldBe(2);
        // ScrollPageSize is what one read carries, BatchSize is what one write carries
        fixture.Fake.UpsertBatchSizes.ShouldBe([100, 100, 100, 100, 100, 1]);
        stats.CopiedPoints.ShouldBe(501);
        stats.TargetPoints.ShouldBe(501);
    }

    // The one failure a scroll buffer cannot see: a batch the server acknowledged and then lost. Only the
    // count re-read off the target collection catches it, and the contract says that is a failed command.
    [Fact]
    public async Task CopyAsync_BatchTheServerAcknowledgedButLost_FailsWithAllThreeNumbersPrinted()
    {
        var fixture = NewFixture(SourcePoints(5));
        fixture.Fake.SilentlyDroppedUpsertBatch = 1;

        var ex = await Should.ThrowAsync<WorkspaceException>(() => CopyAsync(fixture));

        ex.Code.ShouldBe(WorkspaceException.WorktreeVectorMismatchCode);
        ex.Message.ShouldContain($"'{SourceCollection}' held 5 point(s)");
        ex.Message.ShouldContain("wrote 5");
        ex.Message.ShouldContain($"'{ChildCollection}' counts 0 after the writes");
        ex.Message.ShouldContain($"squeezr drop --store-key '{TargetStoreKey}'");
        ex.Message.ShouldContain("--reindex");
    }

    [Fact]
    public async Task CopyAsync_TargetAlreadyHoldsAPointTheSourceDoesNot_RefusesRatherThanCallingItACopy()
    {
        var fixture = NewFixture(SourcePoints(2));
        fixture.Fake.Seed(ChildCollection, 2, [QdrantFakeServer.Point(999, "hash-stray", [0f, 0f])]);

        var ex = await Should.ThrowAsync<WorkspaceException>(() => CopyAsync(fixture));

        ex.Code.ShouldBe(WorkspaceException.WorktreeVectorMismatchCode);
        // a lane holding a point the parent never had is not a copy, and no number read off the scroll
        // buffer can say otherwise
        ex.Message.ShouldContain("counts 3 after the writes");
    }

    [Fact]
    public async Task CopyAsync_VectorStoreUnreachable_ReportsNotCopiedAndAttemptsNoWrite()
    {
        var fixture = NewFixture(SourcePoints(4), unavailable: true);

        var stats = await CopyAsync(fixture, "http://qdrant.unreachable");

        stats.Copied.ShouldBeFalse();
        stats.Reason.ShouldNotBeNull().ShouldContain("did not answer");
        stats.Reason.ShouldContain("http://qdrant.unreachable");
        stats.Reason.ShouldContain("squeezr services up");
        stats.Reason.ShouldContain("--reindex");
        stats.SourcePoints.ShouldBe(0);
        stats.TargetPoints.ShouldBe(0);
        // the availability probe is the whole conversation: no count, no scroll, no write attempted
        fixture.Fake.Paths.ShouldBe(["/qdrant.Collections/List"]);
        fixture.Fake.Holds(ChildCollection).ShouldBeFalse();
    }

    [Fact]
    public async Task CopyAsync_ParentNeverWroteACollection_ReportsNotCopiedAndMintsNoChild()
    {
        var fixture = NewFixture([], seedSource: false);

        var stats = await CopyAsync(fixture);

        stats.Copied.ShouldBeFalse();
        stats.Reason.ShouldNotBeNull().ShouldContain(SourceCollection);
        stats.Reason.ShouldContain("squeezr index");
        fixture.Fake.Holds(ChildCollection).ShouldBeFalse(
            "a lane with nothing to inherit gets no collection of its own");
    }

    [Fact]
    public async Task CopyAsync_SourceCollectionExistsButHoldsNothing_ReportsNotCopiedAndMintsNoChild()
    {
        var fixture = NewFixture([]);

        var stats = await CopyAsync(fixture);

        stats.Copied.ShouldBeFalse();
        stats.Reason.ShouldNotBeNull().ShouldContain("holds no points");
        fixture.Fake.Holds(ChildCollection).ShouldBeFalse();
    }

    [Fact]
    public async Task CopyAsync_RepeatedOverTheSameLane_LandsTheSameTripleAndMintsNoSecondCollection()
    {
        var fixture = NewFixture(SourcePoints(6));

        var first = await CopyAsync(fixture);
        var second = await CopyAsync(fixture);

        // content-derived ids make the copy converge: a retry finishes a half-run instead of doubling it
        second.ShouldBe(first);
        fixture.Fake.CollectionNames().ShouldBe(
            new[] { ChildCollection, SourceCollection }.Order(StringComparer.Ordinal).ToList());
        fixture.Fake.Points(ChildCollection).Count.ShouldBe(6);
        // one tree one collection: the second pass reused what the first one made
        fixture.Fake.Paths.Count(path => path == "/qdrant.Collections/Create").ShouldBe(1);
    }

    // The absence proof. An embedding round trip cannot be answered here at all — the fake fails the test on
    // any path it does not know — and every path this pass may take is named below. Nothing in it re-embeds,
    // deletes, aliases or renames.
    [Fact]
    public async Task CopyAsync_TakesOnlyTheQdrantCallsACopyNeedsAndReachesNoProvider()
    {
        var fixture = NewFixture(SourcePoints(2));

        await CopyAsync(fixture);

        fixture.Fake.Paths.Distinct().Order(StringComparer.Ordinal).ShouldBe(
            [
                "/qdrant.Collections/CollectionExists",
                "/qdrant.Collections/Create",
                "/qdrant.Collections/Get",
                "/qdrant.Collections/List",
                "/qdrant.Points/Count",
                "/qdrant.Points/CreateFieldIndex",
                "/qdrant.Points/Scroll",
                "/qdrant.Points/Upsert",
            ]);
    }

    // The source belongs to a running main worktree, so it can answer differently on the two sides of the
    // walk. That is a measurement of a moving collection, not a contract this pass broke, and refusing over it
    // would strand the lane at every session start until a human intervenes.
    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task CopyAsync_SourceMovedDuringTheWalk_ReportsDriftWithBothSourceNumbersAndKeepsWhatLanded(
        int moves)
    {
        var fixture = NewFixture(SourcePoints(4));
        fixture.Fake.SourceMovesByOnScroll = moves;

        var stats = await CopyAsync(fixture);

        stats.Copied.ShouldBeFalse();
        stats.Code.ShouldBe(WorkspaceException.WorktreeVectorDriftCode);
        stats.SourcePoints.ShouldBe(4);
        stats.SourcePointsAfterWalk.ShouldBe(4 + moves);
        stats.CopiedPoints.ShouldBe(4);
        stats.TargetPoints.ShouldBe(stats.CopiedPoints,
            "the number this pass owns is the one that has to agree, whatever the parent was doing meanwhile");
        stats.Reason.ShouldNotBeNull().ShouldContain("not a gap in this one",
            customMessage: "a source that moved is a measurement of a busy parent, not a verdict on this lane");
        stats.Reason.ShouldContain(RemedyText.VectorsReconcileApply);
    }

    // A store that greeted the probe and died on the next question is the ordinary infra-not-in-the-room
    // state of a session start, and this verb's own report on stdout is the surface a hook parses. A raw gRPC
    // exception there costs the whole machine surface, so both reads that precede any write are answered as
    // a reported refusal naming what was being read, with the lane's collection left uncreated.
    [Theory]
    [InlineData("/qdrant.Collections/Get", "read the source collection's vector size")]
    [InlineData("/qdrant.Points/Count", "count the points in the source collection")]
    public async Task CopyAsync_StoreDiesBeforeAnyWrite_ReportsTheShapeWithoutTouchingTheLane(
        string diesOnPath,
        string phaseInReason)
    {
        var fixture = NewFixture(SourcePoints(3));
        fixture.Fake.DiesOnPath = diesOnPath;

        var stats = await CopyAsync(fixture);

        stats.Copied.ShouldBeFalse();
        stats.Reason.ShouldNotBeNull().ShouldContain(phaseInReason);
        stats.Reason.ShouldContain("nothing was written");
        fixture.Fake.Paths.ShouldNotContain("/qdrant.Points/Upsert");
        fixture.Fake.Holds(ChildCollection).ShouldBeFalse();
    }

    // The same death once writes have begun is the copy stopping, not the store being absent: the lane holds
    // half a collection and the database already calls these chunks embedded. That is said with the phase it
    // stopped on and the numbers it stopped at.
    [Fact]
    public async Task CopyAsync_StoreDiesOnTheConfirmingCount_StopsAndNamesThePhaseAndTheNumbers()
    {
        var fixture = NewFixture(SourcePoints(3));
        fixture.Fake.DiesOnCountRead = 3;

        // the shape, not the class: a raw Grpc.Core.RpcException escaping here reads as this refusal missing
        // its code and its numbers, which is exactly what the session-start hook cannot parse
        var ex = await Should.ThrowAsync<WorkspaceException>(() => CopyAsync(fixture));

        ex.Code.ShouldBe(WorkspaceException.WorktreeVectorMismatchCode);
        ex.Message.ShouldContain("count the worktree's collection after the writes");
        ex.Message.ShouldContain("after writing 3 of the 3 point(s)");
        ex.Message.ShouldContain(RemedyText.VectorsReconcileApply);
    }

    // A lane collection minted under another geometry is not a model question: the size this path asks for came
    // off the parent's collection, and re-embedding the parent would not touch the lane's mismatched shell.
    [Fact]
    public async Task CopyAsync_ChildCollectionHasAnotherSize_RefusesForTheLanesOwnDoor()
    {
        var fixture = NewFixture(SourcePoints(3));
        fixture.Fake.Seed(ChildCollection, 3, []);

        var ex = await Should.ThrowAsync<WorkspaceException>(() => CopyAsync(fixture));

        ex.Code.ShouldBe(WorkspaceException.WorktreeVectorDimensionCode);
        ex.Message.ShouldContain(SourceCollection);
        ex.Message.ShouldContain(ChildCollection);
        ex.Message.ShouldContain("size 3");
        ex.Message.ShouldContain("2-dimensional");
        ex.Message.ShouldContain($"drop --store-key '{TargetStoreKey}'");
        ex.Message.ShouldContain("--reindex");
        ex.Message.ShouldNotContain("--reembed",
            customMessage: "the parent's model is not what disagrees here, and re-embedding it is not this lane's remedy");
        ex.Message.ShouldNotContain("active embedding model");
    }

    // The fidelity the backfill pass rides on, pinned where the copy decides it: a source point written before
    // the kind key existed lands without the key, not with an empty one, because an empty keyword is a VALUE
    // to Qdrant's IsEmpty filter and would hide the lane's legacy points forever.
    [Fact]
    public async Task CopyAsync_SourcePointHasNoKind_WritesItWithoutTheKindKey()
    {
        var points = SourcePoints(2);
        var legacyId = QdrantVectorStore.PointIdFromChunkHash("hash-legacy");
        points.Add(QdrantFakeServer.Point(legacyId, "hash-legacy", [0.9f, 0.1f], kind: ""));
        var fixture = NewFixture([.. points.OrderBy(point => point.Id)]);

        var stats = await CopyAsync(fixture);

        stats.CopiedPoints.ShouldBe(3);
        var legacy = fixture.Fake.Points(ChildCollection).Single(point => point.Id == legacyId);
        legacy.Payload.ContainsKey("kind").ShouldBeFalse();
        legacy.Payload["chunkHash"].StringValue.ShouldBe("hash-legacy");
        fixture.Fake.Points(ChildCollection).Count(point => point.Payload.ContainsKey("kind")).ShouldBe(2);
    }

    // Foreign points are a counted gap, not a refusal: one point in the parent that this product did not write
    // is the parent's business, and a session-start verb that stopped on it would stop forever, because the
    // file arms already landed and the arm never re-runs. The lane's own rows still all cross.
    [Fact]
    public async Task CopyAsync_SourceHoldsAPointThisProductDidNotWrite_CountsItAndCopiesTheRest()
    {
        var points = SourcePoints(2);
        points.Add(QdrantFakeServer.PointWithRawPayload(
            900UL, [0.3f, 0.3f], ("chunkHash", new Value { StringValue = "not-ours" })));
        var fixture = NewFixture([.. points.OrderBy(point => point.Id)]);

        var stats = await CopyAsync(fixture);

        stats.Copied.ShouldBeFalse();
        stats.Code.ShouldBe(WorkspaceException.VectorPointForeignCode);
        stats.ForeignPoints.ShouldBe(1);
        stats.SourcePoints.ShouldBe(3);
        stats.CopiedPoints.ShouldBe(2);
        stats.TargetPoints.ShouldBe(2);
        fixture.Fake.Points(ChildCollection).Count.ShouldBe(2, "a foreign point is skipped, never imitated");
        fixture.Fake.Points(ChildCollection).Any(point => point.Id == 900UL).ShouldBeFalse();
        stats.Reason.ShouldNotBeNull().ShouldContain("900");
    }

    // Finding 5's arm, pinned rather than decorative: a batch acknowledged and applied late is the state a
    // replicated endpoint answers with, and the pass that declares its invariant off the first read would
    // refuse a copy that had in fact landed. The re-count is bounded, so the pin is that it settles inside
    // the budget and says nothing about a source that never moved.
    [Fact]
    public async Task CopyAsync_TargetCountLagsOneReadBehindTheBatch_RetriesAndStillReportsTheCopy()
    {
        var fixture = NewFixture(SourcePoints(5));
        fixture.Fake.LaggingUpsertBatch = 1;

        var stats = await CopyAsync(fixture);

        stats.Copied.ShouldBeTrue();
        stats.SourcePoints.ShouldBe(5);
        stats.CopiedPoints.ShouldBe(5);
        stats.TargetPoints.ShouldBe(5);
        stats.Reason.ShouldBeNull("a disagreement this pass settled is not a fact worth reporting");
        fixture.Fake.Points(ChildCollection).Count.ShouldBe(5);
    }

    private static WorktreeVectorCopyRequest Request() => new(SourceRoot, ChildRoot, TargetStoreKey);

    private static Task<WorktreeVectorCopyStats> CopyAsync(Fixture fixture, string endpoint = "http://qdrant.test") =>
        WorktreeVectorCopier.CopyAsync(
            fixture.Source,
            fixture.Target,
            Request(),
            endpoint,
            CancellationToken.None);

    // Ids are the ones the product mints: the point id is the chunk hash, so a copy is measured against the
    // ids a later pass will look its points up by.
    private static List<QdrantFakeServer.StoredPoint> SourcePoints(int count) =>
        [.. Enumerable.Range(1, count)
            .Select(index => QdrantFakeServer.Point(
                QdrantVectorStore.PointIdFromChunkHash($"hash-{index}"),
                $"hash-{index}",
                [0.1f * index, 0.2f * index],
                kind: index % 3 == 0 ? "docs" : "code",
                fileId: (uint)(17 + index),
                startLine: index,
                endLine: index + 4))
            .OrderBy(point => point.Id)];

    private static Fixture NewFixture(
        IReadOnlyList<QdrantFakeServer.StoredPoint> sourcePoints,
        bool unavailable = false,
        bool seedSource = true)
    {
        var fake = new QdrantFakeServer { Unavailable = unavailable };
        if (seedSource)
        {
            fake.Seed(SourceCollection, 2, sourcePoints);
        }

        var client = new QdrantClient(new QdrantGrpcClient(GrpcChannel.ForAddress(
            "http://localhost:1",
            new GrpcChannelOptions { HttpHandler = fake, Credentials = ChannelCredentials.Insecure })));

        return new Fixture(
            fake,
            new QdrantVectorStore(client, SourceCollection),
            new QdrantVectorStore(client, ChildCollection));
    }

    private sealed record Fixture(
        QdrantFakeServer Fake,
        QdrantVectorStore Source,
        QdrantVectorStore Target);
}
