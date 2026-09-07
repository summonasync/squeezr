using System.Diagnostics;
using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Configuration;

namespace Squeezr.Tests.Embeddings;

public class QdrantVectorStoreTests
{
    [Fact]
    public void GetCollectionName_IsDeterministicPerWorkspaceRoot()
    {
        var first = QdrantVectorStore.GetCollectionName(@"E:\ws\demo");
        var second = QdrantVectorStore.GetCollectionName(@"E:\ws\demo");

        first.ShouldBe(second);
        first.ShouldStartWith("squeezr-");
        first.Length.ShouldBe("squeezr-".Length + 12);
    }

    [Fact]
    public void GetCollectionName_KnownWorkspaceRoot_MatchesExpectedHash()
    {
        var name = QdrantVectorStore.GetCollectionName(@"E:\ws\demo");

        name.ShouldBe("squeezr-cb1c622c72c6");
    }

    [Fact]
    public void GetCollectionName_DifferentRoots_Differ()
    {
        var a = QdrantVectorStore.GetCollectionName(@"E:\ws\a");
        var b = QdrantVectorStore.GetCollectionName(@"E:\ws\b");

        a.ShouldNotBe(b);
    }

    [Fact]
    public void PointIdFromChunkHash_KnownHash_MatchesLittleEndianFirst8Bytes()
    {
        var id = QdrantVectorStore.PointIdFromChunkHash("abc");

        id.ShouldBe(16919744041952114874UL);
    }

    [Fact]
    public void PointIdFromChunkHash_IsStableAcrossCalls()
    {
        var first = QdrantVectorStore.PointIdFromChunkHash("deadbeefcafe");
        var second = QdrantVectorStore.PointIdFromChunkHash("deadbeefcafe");

        first.ShouldBe(second);
    }

    [Fact]
    public void PointIdFromChunkHash_DifferentHashes_Differ()
    {
        var a = QdrantVectorStore.PointIdFromChunkHash("chunk-1");
        var b = QdrantVectorStore.PointIdFromChunkHash("chunk-2");

        a.ShouldNotBe(b);
    }

    [Fact]
    public async Task IsAvailable_DeadEndpoint_ReturnsFalseWithoutThrowing()
    {
        var store = new QdrantVectorStore(
            new QdrantConfig { Endpoint = "http://localhost:1" },
            TestDirs.NewTempDir());

        var sw = Stopwatch.StartNew();
        var available = await store.IsAvailableAsync(CancellationToken.None);
        sw.Stop();

        available.ShouldBeFalse();
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task CountAsync_CollectionMissing_ReturnsZero()
    {
        var store = StubbedStore((int)StatusCode.NotFound);

        var count = await store.CountAsync(CancellationToken.None);

        count.ShouldBe(0);
    }

    [Fact]
    public async Task CountAsync_ServerError_Throws()
    {
        var store = StubbedStore((int)StatusCode.Internal);

        await Should.ThrowAsync<RpcException>(() => store.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeleteCollectionAsync_ServerError_SwallowsAsBestEffort()
    {
        var store = StubbedStore((int)StatusCode.Internal);

        await store.DeleteCollectionAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeleteCollectionAsync_CanceledToken_PropagatesCancellation()
    {
        var store = new QdrantVectorStore(
            new QdrantConfig { Endpoint = "http://localhost:1" },
            TestDirs.NewTempDir());

        await Should.ThrowAsync<OperationCanceledException>(
            () => store.DeleteCollectionAsync(new CancellationToken(canceled: true)));
    }

    private static QdrantVectorStore StubbedStore(int grpcStatus)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([])
        };
        response.Headers.TryAddWithoutValidation("content-type", "application/grpc");
        response.Headers.TryAddWithoutValidation("grpc-status", grpcStatus.ToString());

        var handler = new StubHttpMessageHandler(response);
        var channel = GrpcChannel.ForAddress("http://localhost:1", new GrpcChannelOptions
        {
            HttpHandler = handler,
            Credentials = ChannelCredentials.Insecure
        });

        return new QdrantVectorStore(new QdrantClient(new QdrantGrpcClient(channel)), "squeezr-test");
    }
}
