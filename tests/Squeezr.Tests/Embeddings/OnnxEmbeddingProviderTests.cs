using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Embeddings;

namespace Squeezr.Tests.Embeddings;

public class OnnxEmbeddingProviderTests
{
    private static (string ModelPath, string TokenizerDir)? FindBundledModel()
    {
        var modelsDir = Path.Combine(AppContext.BaseDirectory, "Models");
        var modelPath = Path.Combine(modelsDir, "bge-code-v1.onnx");
        var tokenizerDir = Path.Combine(modelsDir, "bge-code-v1");
        return File.Exists(modelPath) && File.Exists(Path.Combine(tokenizerDir, "vocab.json"))
            ? (modelPath, tokenizerDir)
            : null;
    }

    [Fact]
    public async Task Provider_ReturnsConfiguredDimensionsAndUnitVectors()
    {
        var bundled = FindBundledModel();
        if (bundled is null)
            return;

        var provider = new OnnxEmbeddingProvider(
            bundled.Value.ModelPath,
            bundled.Value.TokenizerDir,
            EmbeddingModelProfiles.BgeCodeV1);

        provider.Dimensions.ShouldBe(1536);

        var vectors = await provider.EmbedAsync(["public class Foo { }", "def bar(): pass"], EmbeddingUsage.Document, CancellationToken.None);

        vectors.Length.ShouldBe(2);
        vectors[0].Length.ShouldBe(1536);
        vectors[1].Length.ShouldBe(1536);
        Norm(vectors[0]).ShouldBe(1.0, 1e-4);
        Norm(vectors[1]).ShouldBe(1.0, 1e-4);
        vectors[0].ShouldNotBe(vectors[1]);
    }

    [Fact]
    public void Dimensions_ComeFromProfileOrOverride()
    {
        var encoder = new StubTextEncoder(7);
        var session = new StubOnnxSession((_, s, _) => s);
        var provider = new OnnxEmbeddingProvider(session, encoder, EmbeddingModelProfiles.NomicEmbedTextV15);

        provider.Dimensions.ShouldBe(768);

        var overridden = new OnnxEmbeddingProvider(session, encoder, EmbeddingModelProfiles.NomicEmbedTextV15, dimensionsOverride: 16);
        overridden.Dimensions.ShouldBe(16);
    }

    [Fact]
    public async Task EmbedAsync_NomicDocument_SendsDocumentPrefixedTextToEncoder()
    {
        var encoder = new StubTextEncoder(5, 6);
        var session = new StubOnnxSession((_, s, _) => s);
        var provider = new OnnxEmbeddingProvider(session, encoder, EmbeddingModelProfiles.NomicEmbedTextV15);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None);

        encoder.EncodedTexts.ShouldBe(["search_document: hello"]);
    }

    [Fact]
    public async Task EmbedAsync_NomicQuery_SendsQueryPrefixedTextToEncoder()
    {
        var encoder = new StubTextEncoder(5, 6);
        var session = new StubOnnxSession((_, s, _) => s);
        var provider = new OnnxEmbeddingProvider(session, encoder, EmbeddingModelProfiles.NomicEmbedTextV15);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Query, CancellationToken.None);

        encoder.EncodedTexts.ShouldBe(["search_query: hello"]);
    }

    [Fact]
    public async Task EmbedAsync_BgeProfile_SendsRawTextWithoutPrefix()
    {
        var encoder = new StubTextEncoder(5, 6);
        var session = new StubOnnxSession((_, s, _) => s);
        var provider = new OnnxEmbeddingProvider(session, encoder, EmbeddingModelProfiles.BgeCodeV1);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Query, CancellationToken.None);

        encoder.EncodedTexts.ShouldBe(["hello"]);
    }

    [Fact]
    public async Task EmbedAsync_NomicMeanPool_AveragesMaskedTokensAndNormalizes()
    {
        var encoder = new StubTextEncoder(7, 8, 9);
        var session = new StubOnnxSession((_, s, h) => (s + 1) * (h + 1));
        var provider = new OnnxEmbeddingProvider(session, encoder, EmbeddingModelProfiles.NomicEmbedTextV15);

        var vectors = await provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None);

        var norm = Math.Sqrt(20.0);
        vectors[0][0].ShouldBe((float)(2.0 / norm), 1e-6);
        vectors[0][1].ShouldBe((float)(4.0 / norm), 1e-6);
        Norm(vectors[0]).ShouldBe(1.0, 1e-6);

        var ids = session.CapturedInputIds().Single();
        ids.ShouldBe([7L, 8L, 9L]);
        session.CapturedMask().Single().ShouldBe([1L, 1L, 1L]);
    }

    [Fact]
    public async Task EmbedAsync_BgeLastToken_PicksLastTokenAndNormalizes()
    {
        var encoder = new StubTextEncoder(7, 8, 9);
        var session = new StubOnnxSession((_, s, h) => s * 10 + h);
        var provider = new OnnxEmbeddingProvider(session, encoder, EmbeddingModelProfiles.BgeCodeV1);

        var vectors = await provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None);

        var norm = Math.Sqrt((20.0 * 20.0) + (21.0 * 21.0));
        vectors[0][0].ShouldBe((float)(20.0 / norm), 1e-6);
        vectors[0][1].ShouldBe((float)(21.0 / norm), 1e-6);
        Norm(vectors[0]).ShouldBe(1.0, 1e-6);
    }

    [Fact]
    public async Task EmbedAsync_TruncatesToMaxSequenceLength()
    {
        var encoder = new StubTextEncoder(1, 2, 3, 4, 5);
        var session = new StubOnnxSession((_, s, _) => s);
        var provider = new OnnxEmbeddingProvider(
            session,
            encoder,
            EmbeddingModelProfiles.NomicEmbedTextV15,
            maxSequenceLength: 3);

        await provider.EmbedAsync(["hello"], EmbeddingUsage.Document, CancellationToken.None);

        session.CapturedInputIds().Single().ShouldBe([1L, 2L, 3L]);
    }

    private static double Norm(float[] vector)
    {
        var sum = 0.0;
        foreach (var v in vector)
            sum += v * v;
        return Math.Sqrt(sum);
    }

    private sealed class StubTextEncoder : IEmbeddingTextEncoder
    {
        private readonly int[] _ids;

        public StubTextEncoder(params int[] ids)
        {
            _ids = ids;
            FallbackTokenId = 99;
        }

        public int FallbackTokenId { get; }

        public List<string> EncodedTexts { get; } = [];

        public IReadOnlyList<int> EncodeToIds(string text)
        {
            EncodedTexts.Add(text);
            return _ids;
        }
    }

    private sealed class StubOnnxSession : IOnnxSession
    {
        private readonly Func<int, int, int, float> _hiddenValue;
        private readonly List<IReadOnlyList<long>[]> _captured = [];

        public StubOnnxSession(Func<int, int, int, float> hiddenValue)
            => _hiddenValue = hiddenValue;

        public IReadOnlyCollection<string> InputNames { get; } = ["input_ids", "attention_mask"];

        public IReadOnlyList<long>[] CapturedInputIds() => _captured.Select(run => run[0]).ToArray();

        public IReadOnlyList<long>[] CapturedMask() => _captured.Select(run => run[1]).ToArray();

        public DenseTensor<float> Run(IReadOnlyCollection<NamedOnnxValue> inputs)
        {
            var idsTensor = inputs.First(i => i.Name == "input_ids").AsTensor<long>()
                ?? throw new InvalidOperationException("input_ids tensor is missing.");
            var maskTensor = inputs.First(i => i.Name == "attention_mask").AsTensor<long>()
                ?? throw new InvalidOperationException("attention_mask tensor is missing.");

            _captured.Add([TensorValues(idsTensor), TensorValues(maskTensor)]);

            var batch = idsTensor.Dimensions[0];
            var seq = idsTensor.Dimensions[1];
            var hidden = new DenseTensor<float>([batch, seq, 2]);
            for (var b = 0; b < batch; b++)
            {
                for (var s = 0; s < seq; s++)
                {
                    hidden[b, s, 0] = _hiddenValue(b, s, 0);
                    hidden[b, s, 1] = _hiddenValue(b, s, 1);
                }
            }

            return hidden;
        }

        private static long[] TensorValues(Tensor<long> tensor)
        {
            var dims = tensor.Dimensions;
            var values = new long[tensor.Length];
            var i = 0;
            for (var b = 0; b < dims[0]; b++)
            {
                for (var s = 0; s < dims[1]; s++)
                {
                    values[i++] = tensor[b, s];
                }
            }
            return values;
        }

        public void Dispose()
        {
        }
    }
}
