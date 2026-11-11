using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Shouldly;
using Squeezr.Cli.Embeddings;

namespace Squeezr.Tests.Embeddings;

// The copy reads points back the way it wrote them: one page carrying vectors AND payload together, the
// product's own payload keys, and a kind that was never written staying unwritten. These are the three
// places a scroll-and-put can quietly lose fidelity, so each is pinned at the wire rather than assumed from
// the caller.
public class QdrantVectorStoreCopyTests
{
    private const string Collection = "squeezr-copy-test";

    [Fact]
    public async Task ScrollVectorPointsAsync_AskedForVectorsAndPayloadInOnePass_ReturnsThePointWhole()
    {
        var handler = new GrpcStubHttpMessageHandler(new Dictionary<string, IMessage>
        {
            ["/qdrant.Points/Scroll"] = new ScrollResponse
            {
                Result = { StoredPoint(7, [0.5f, 0.25f]) },
            },
        });
        var store = Store(handler);

        var page = await store.ScrollVectorPointsAsync(null, CancellationToken.None);

        // the record's own equality cannot compare a float array, so each coordinate is named
        var point = page.Points.ShouldHaveSingleItem();
        point.Id.ShouldBe(7UL);
        point.Vector.ShouldBe([0.5f, 0.25f]);
        point.ChunkHash.ShouldBe("hash-7");
        point.FileId.ShouldBe(17u);
        point.RepoId.ShouldBe(3u);
        point.StartLine.ShouldBe(1);
        point.EndLine.ShouldBe(9);
        point.Kind.ShouldBe("code");
        page.NextOffset.ShouldBeNull();
        var request = ScrollPoints.Parser.ParseFrom(
            handler.Requests.Single(r => r.Path == "/qdrant.Points/Scroll").Body.AsSpan(5));
        request.WithPayload.Enable.ShouldBeTrue();
        request.WithVectors.Enable.ShouldBeTrue(
            "a copy that asked for payload without vectors would have to re-read every point by id");
        request.Limit.ShouldBe(500u);
        request.CollectionName.ShouldBe(Collection);
    }

    [Fact]
    public async Task ScrollVectorPointsAsync_PointWrittenBeforeKindExists_KeepsKindAbsent()
    {
        var withoutKind = new RetrievedPoint
        {
            Id = new PointId { Num = 11 },
            Vectors = new VectorsOutput
            {
                Vector = new VectorOutput { Dense = new DenseVector { Data = { 1f } } },
            },
            Payload =
            {
                ["chunkHash"] = "hash-11",
                ["fileId"] = (long)21,
                ["repoId"] = (long)3,
                ["startLine"] = (long)1,
                ["endLine"] = (long)9,
            },
        };
        var handler = new GrpcStubHttpMessageHandler(new Dictionary<string, IMessage>
        {
            ["/qdrant.Points/Scroll"] = new ScrollResponse { Result = { withoutKind } },
        });
        var store = Store(handler);

        var point = (await store.ScrollVectorPointsAsync(null, CancellationToken.None)).Points.Single();

        point.Kind.ShouldBeNull(
            "an absent kind copied as an empty string becomes a value the backfill pass reads as repaired");
    }

    // A point this product did not write cannot be named as one of ours, and it is not an error channel either:
    // one such point in a parent's collection is a fact about that collection, not a reason to stop a session
    // start. It leaves the page through ForeignPoints so the copy can count and name it later.
    [Fact]
    public async Task ScrollVectorPointsAsync_PointThatThisProductDidNotWrite_LeavesThePageNamedAndApart()
    {
        var foreign = new RetrievedPoint
        {
            Id = new PointId { Num = 99 },
            Vectors = new VectorsOutput
            {
                Vector = new VectorOutput { Dense = new DenseVector { Data = { 1f } } },
            },
        };
        var handler = new GrpcStubHttpMessageHandler(new Dictionary<string, IMessage>
        {
            ["/qdrant.Points/Scroll"] = new ScrollResponse { Result = { foreign, StoredPoint(7, [1f, 2f]) } },
        });
        var store = Store(handler);

        var page = await store.ScrollVectorPointsAsync(null, CancellationToken.None);

        var defect = page.ForeignPoints.ShouldHaveSingleItem();
        defect.Id.ShouldBe(99UL);
        defect.Missing.ShouldContain("chunkHash");
        page.Points.ShouldHaveSingleItem().Id.ShouldBe(7UL,
            "a point this product wrote is still copied just because the page also carried a foreign one");
    }

    // The value kind is part of the fidelity check, not only the key's presence: an all-digit chunk hash that
    // came back as a number would copy as an empty hash and mint an id for a point that never existed.
    [Fact]
    public async Task ScrollVectorPointsAsync_PayloadKeyOfTheWrongValueKind_CountsAsForeign()
    {
        var numericHash = new RetrievedPoint
        {
            Id = new PointId { Num = 100 },
            Vectors = new VectorsOutput
            {
                Vector = new VectorOutput { Dense = new DenseVector { Data = { 1f } } },
            },
            Payload =
            {
                ["chunkHash"] = (long)4242,
                ["fileId"] = (long)17,
                ["repoId"] = (long)3,
                ["startLine"] = (long)1,
                ["endLine"] = (long)9,
            },
        };
        var handler = new GrpcStubHttpMessageHandler(new Dictionary<string, IMessage>
        {
            ["/qdrant.Points/Scroll"] = new ScrollResponse { Result = { numericHash } },
        });
        var store = Store(handler);

        var page = await store.ScrollVectorPointsAsync(null, CancellationToken.None);

        page.Points.ShouldBeEmpty();
        page.ForeignPoints.ShouldHaveSingleItem().Missing.ShouldContain("text 'chunkHash'");
    }

    [Fact]
    public async Task ScrollVectorPointsAsync_CursorFromTheServer_RoundTripsAsTheNextOffset()
    {
        var handler = new GrpcStubHttpMessageHandler(new Dictionary<string, IMessage>
        {
            ["/qdrant.Points/Scroll"] = new ScrollResponse
            {
                Result = { StoredPoint(3, [1f]) },
                NextPageOffset = new PointId { Num = 42 },
            },
        });
        var store = Store(handler);

        var page = await store.ScrollVectorPointsAsync(7, CancellationToken.None);

        page.NextOffset.ShouldBe(42UL);
        var request = ScrollPoints.Parser.ParseFrom(
            handler.Requests.Single(r => r.Path == "/qdrant.Points/Scroll").Body.AsSpan(5));
        request.Offset.Num.ShouldBe(7UL);
    }

    [Fact]
    public async Task ScrollVectorPointsAsync_CollectionAbsent_ReturnsEmptyPageWithoutThrowing()
    {
        var store = Store((int)StatusCode.NotFound);

        var page = await store.ScrollVectorPointsAsync(null, CancellationToken.None);

        page.Points.ShouldBeEmpty();
        page.NextOffset.ShouldBeNull();
    }

    [Fact]
    public async Task GetVectorDimensionsAsync_ExistingCollection_AnswersWithTheSizeItWasCreatedWith()
    {
        var handler = new GrpcStubHttpMessageHandler(new Dictionary<string, IMessage>
        {
            ["/qdrant.Collections/Get"] = new GetCollectionInfoResponse
            {
                Result = new CollectionInfo
                {
                    Config = new CollectionConfig
                    {
                        Params = new CollectionParams
                        {
                            VectorsConfig = new VectorsConfig { Params = new VectorParams { Size = 768 } },
                        },
                    },
                },
            },
        });
        var store = Store(handler);

        var size = await store.GetVectorDimensionsAsync(CancellationToken.None);

        size.ShouldBe(768UL);
    }

    [Fact]
    public async Task GetVectorDimensionsAsync_CollectionAbsent_ReturnsNull()
    {
        var store = Store((int)StatusCode.NotFound);

        var size = await store.GetVectorDimensionsAsync(CancellationToken.None);

        size.ShouldBeNull();
    }

    [Fact]
    public async Task UpsertAsync_PointWithoutKind_WritesEveryOtherPayloadKeyAndNoKind()
    {
        var handler = new GrpcStubHttpMessageHandler(new Dictionary<string, IMessage>());
        var store = Store(handler);

        await store.UpsertAsync(
            [new Squeezr.Core.Embeddings.VectorPoint(5, [1f, 2f], "hash-5", 15, 3, 1, 9, null)],
            CancellationToken.None);

        var request = UpsertPoints.Parser.ParseFrom(
            handler.Requests.Single(r => r.Path == "/qdrant.Points/Upsert").Body.AsSpan(5));
        var payload = request.Points.Single().Payload;
        payload.ContainsKey("kind").ShouldBeFalse();
        payload["chunkHash"].StringValue.ShouldBe("hash-5");
        payload["fileId"].IntegerValue.ShouldBe(15L);
    }

    // The pair the copy depends on: what one pass writes is what one pass reads back, ids included. A point
    // id derived from content that came back different would break every later upsert and delete by id.
    [Fact]
    public async Task ScrollVectorPointsAsync_ReadsBackEveryPointUpsertAsyncWrote()
    {
        var fake = new QdrantFakeServer();
        var store = Store(fake);
        var written = Enumerable.Range(1, 3)
            .Select(index => new Squeezr.Core.Embeddings.VectorPoint(
                (ulong)index,
                [0.1f * index, 0.2f * index],
                $"hash-{index}",
                (uint)(10 + index),
                3,
                index,
                index + 4,
                index % 2 == 0 ? "docs" : "code"))
            .ToList();

        await store.EnsureCollectionAsync(2, CancellationToken.None);
        await store.UpsertAsync(written, CancellationToken.None);
        var pagePoints = (await store.ScrollVectorPointsAsync(null, CancellationToken.None)).Points;

        var read = pagePoints.OrderBy(point => point.Id).ToList();
        read.Select(point => point.Id).ShouldBe(written.Select(point => point.Id));
        read.Select(point => point.ChunkHash).ShouldBe(written.Select(point => point.ChunkHash));
        read.Select(point => point.Kind).ShouldBe(written.Select(point => point.Kind));
        read.Select((point, index) => point.Vector.SequenceEqual(written[index].Vector)).ShouldBe([true, true, true]);
    }

    private static RetrievedPoint StoredPoint(ulong id, float[] vector) => new()
    {
        Id = new PointId { Num = id },
        Vectors = new VectorsOutput
        {
            Vector = new VectorOutput { Dense = new DenseVector { Data = { vector } } },
        },
        Payload =
        {
            ["chunkHash"] = $"hash-{id}",
            ["fileId"] = (long)(id + 10),
            ["repoId"] = (long)3,
            ["startLine"] = (long)1,
            ["endLine"] = (long)9,
            ["kind"] = "code",
        },
    };

    private static QdrantVectorStore Store(HttpMessageHandler handler) =>
        new(Client(handler), Collection);

    private static QdrantVectorStore Store(int grpcStatus)
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]),
        };
        response.Content.Headers.TryAddWithoutValidation("content-type", "application/grpc");
        response.Headers.TryAddWithoutValidation("grpc-status", grpcStatus.ToString());
        return Store(new StubHttpMessageHandler(response));
    }

    private static QdrantClient Client(HttpMessageHandler handler) =>
        new(new QdrantGrpcClient(GrpcChannel.ForAddress("http://localhost:1", new GrpcChannelOptions
        {
            HttpHandler = handler,
            Credentials = ChannelCredentials.Insecure,
        })));
}
