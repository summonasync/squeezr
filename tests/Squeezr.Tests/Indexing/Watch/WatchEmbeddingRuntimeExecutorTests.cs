using Shouldly;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Semantic.Configuration;
using Squeezr.Semantic.Indexing.Watch;

namespace Squeezr.Tests.Indexing.Watch;

[Collection("ProcessGlobals")]
public class WatchEmbeddingRuntimeExecutorTests : IDisposable
{
    private const string ApiKeyEnv = "SQUEEZR_TESTS_EXECUTOR_KEY";

    public WatchEmbeddingRuntimeExecutorTests() =>
        Environment.SetEnvironmentVariable(ApiKeyEnv, "test-key");

    public void Dispose() =>
        Environment.SetEnvironmentVariable(ApiKeyEnv, null);

    [Fact]
    public void Executor_ProbesTheProviderOnce_PerRuntime()
    {
        var provider = new CountingEvidenceProvider(new EmbeddingExecutor("gpu", null));
        var runtime = new WatchEmbeddingRuntime(provider, new SilentStore(), new EmbeddingProviderResolution("ollama", null));

        runtime.Executor.Device.ShouldBe("gpu");
        runtime.Executor.Device.ShouldBe("gpu");

        provider.ProbeCount.ShouldBe(1, "the verdict is one probe per runtime, every later read replays it");
    }

    [Fact]
    public void Executor_ProviderWithoutEvidence_IsUnknownWithReason()
    {
        var runtime = new WatchEmbeddingRuntime(
            new PlainProvider(), new SilentStore(), new EmbeddingProviderResolution("mystery", null));

        var executor = runtime.Executor;

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldContain("mystery");
    }

    [Fact]
    public void Executor_RemoteProvider_AnswersRemoteWithoutAnyProbe()
    {
        var handler = new ExplodingHandler();
        var provider = new OpenAiCompatibleEmbeddingProvider(
            new OpenAiCompatibleOptions { Endpoint = "http://localhost:9/v1", ApiKeyEnv = ApiKeyEnv },
            TestEmbeddingKeys.UnregisteredOpenAiModel,
            handler);
        var runtime = new WatchEmbeddingRuntime(provider, new SilentStore(), new EmbeddingProviderResolution("openai", null));

        runtime.Executor.Device.ShouldBe("remote");
        runtime.Executor.Reason.ShouldBeNull();
    }

    [Fact]
    public void Executor_ThrowingEvidenceSource_DegradesToUnknownInsteadOfKillingTheCaller()
    {
        var runtime = new WatchEmbeddingRuntime(
            new ThrowingEvidenceProvider(), new SilentStore(), new EmbeddingProviderResolution("ollama", null));

        var executor = runtime.Executor;

        executor.Device.ShouldBe("unknown");
        executor.Reason.ShouldContain("evidence source exploded");
    }

    private sealed class CountingEvidenceProvider(EmbeddingExecutor verdict) : IEmbeddingProvider, IEmbeddingExecutorEvidence
    {
        private int _probes;

        public int ProbeCount => Volatile.Read(ref _probes);

        public int Dimensions => 4;

        public string ModelKey => TestEmbeddingKeys.TestModel;

        public EmbeddingExecutor ProbeExecutor()
        {
            Interlocked.Increment(ref _probes);
            return verdict;
        }

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class PlainProvider : IEmbeddingProvider
    {
        public int Dimensions => 4;

        public string ModelKey => TestEmbeddingKeys.TestModel;

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingEvidenceProvider : IEmbeddingProvider, IEmbeddingExecutorEvidence
    {
        public int Dimensions => 4;

        public string ModelKey => TestEmbeddingKeys.TestModel;

        public EmbeddingExecutor ProbeExecutor() =>
            throw new InvalidOperationException("evidence source exploded");

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class SilentStore : IVectorStore
    {
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);

        public Task EnsureCollectionAsync(int dimensions, CancellationToken ct) => Task.CompletedTask;

        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<VectorHit>> SearchAsync(float[] vector, int topK, VectorSearchFilter filter, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(CancellationToken ct) => Task.FromResult(0L);

        public Task<IReadOnlyList<ulong>> ScrollPointIdsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ulong>>([]);

        public Task<IReadOnlyList<uint>> ScrollFileIdsWithoutKindAsync(uint repoId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<uint>>([]);

        public Task SetKindPayloadsAsync(IReadOnlyDictionary<uint, string> kindByFileId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task DeleteByFileIdsAsync(IReadOnlyList<uint> fileIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteByPointIdsAsync(IReadOnlyList<ulong> pointIds, CancellationToken ct) => Task.CompletedTask;

        public Task DeleteCollectionAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ExplodingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("remote providers must not be probed");
    }
}
