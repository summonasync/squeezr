using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Squeezr.Core.Embeddings;

namespace Squeezr.Cli.Embeddings;

public sealed class OnnxEmbeddingProvider : IEmbeddingProvider, IEmbeddingExecutorEvidence
{
    private readonly int _maxSequenceLength;
    private readonly int _batchSize;
    private readonly EmbeddingModelProfile _profile;
    private readonly Lazy<(IOnnxSession Session, IEmbeddingTextEncoder Encoder)> _runtime;

    public int Dimensions { get; }

    public OnnxEmbeddingProvider(
        string modelPath,
        string tokenizerDir,
        EmbeddingModelProfile profile,
        int? dimensionsOverride = null,
        int maxSequenceLength = 512,
        int batchSize = 32)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Embedding model not found: {modelPath}", modelPath);
        if (dimensionsOverride is <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensionsOverride));

        _profile = profile;
        Dimensions = dimensionsOverride ?? profile.Dimensions;
        _maxSequenceLength = maxSequenceLength;
        _batchSize = Math.Max(1, batchSize);
        _runtime = new Lazy<(IOnnxSession, IEmbeddingTextEncoder)>(() => CreateRuntime(modelPath, tokenizerDir, profile));
    }

    internal OnnxEmbeddingProvider(
        IOnnxSession session,
        IEmbeddingTextEncoder encoder,
        EmbeddingModelProfile profile,
        int? dimensionsOverride = null,
        int maxSequenceLength = 512,
        int batchSize = 32)
    {
        if (dimensionsOverride is <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensionsOverride));
        _profile = profile;
        Dimensions = dimensionsOverride ?? profile.Dimensions;
        _maxSequenceLength = maxSequenceLength;
        _batchSize = Math.Max(1, batchSize);
        _runtime = new Lazy<(IOnnxSession, IEmbeddingTextEncoder)>(() => (session, encoder));
    }

    public EmbeddingExecutor ProbeExecutor()
    {
        try
        {
            return OnnxExecutorEvidence.Derive(OrtEnv.Instance().GetAvailableProviders());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            return new EmbeddingExecutor("unknown", $"onnxruntime provider query failed: {ex.Message}");
        }
    }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, EmbeddingUsage usage = EmbeddingUsage.Document, CancellationToken ct = default)
    {
        var (session, encoder) = _runtime.Value;
        var results = new float[texts.Count][];
        var prefix = _profile.PrefixFor(usage);

        for (var batchStart = 0; batchStart < texts.Count; batchStart += _batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batchEnd = Math.Min(batchStart + _batchSize, texts.Count);
            var batchCount = batchEnd - batchStart;

            var tokenIds = new List<int>[batchCount];
            var maxLen = 1;
            for (var i = 0; i < batchCount; i++)
            {
                var text = prefix is null ? texts[batchStart + i] : prefix + texts[batchStart + i];
                var ids = encoder.EncodeToIds(text.Normalize(NormalizationForm.FormC));
                var truncated = ids.Count > _maxSequenceLength ? ids.Take(_maxSequenceLength).ToList() : ids.ToList();
                tokenIds[i] = truncated.Count == 0 ? [encoder.FallbackTokenId] : truncated;
                maxLen = Math.Max(maxLen, tokenIds[i].Count);
            }

            var inputNames = session.InputNames;
            var inputs = new List<NamedOnnxValue>(inputNames.Count);
            foreach (var name in inputNames)
            {
                if (name.Contains("mask", StringComparison.OrdinalIgnoreCase))
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, BuildLongTensor(batchCount, maxLen, (b, s) => s < tokenIds[b].Count ? 1 : 0)));
                else if (name.Contains("position", StringComparison.OrdinalIgnoreCase))
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, BuildLongTensor(batchCount, maxLen, (_, s) => s)));
                else if (name.Contains("type", StringComparison.OrdinalIgnoreCase))
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, BuildLongTensor(batchCount, maxLen, (_, _) => 0)));
                else
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, BuildLongTensor(batchCount, maxLen, (b, s) => s < tokenIds[b].Count ? tokenIds[b][s] : encoder.FallbackTokenId)));
            }

            var hidden = session.Run(inputs);
            if (hidden.Rank != 3)
                throw new InvalidOperationException($"Expected a [batch, sequence, hidden] output, got rank {hidden.Rank}.");

            var hiddenDim = hidden.Dimensions[2];
            for (var b = 0; b < batchCount; b++)
            {
                var vector = _profile.Pooling == EmbeddingPooling.MeanL2
                    ? MeanPool(hidden, b, tokenIds[b].Count, hiddenDim)
                    : LastToken(hidden, b, tokenIds[b].Count, hiddenDim);
                NormalizeInPlace(vector);
                results[batchStart + b] = vector;
            }
        }

        return Task.FromResult(results);
    }

    private static float[] LastToken(Tensor<float> hidden, int batch, int tokenCount, int hiddenDim)
    {
        var vector = new float[hiddenDim];
        var lastIndex = tokenCount - 1;
        for (var h = 0; h < hiddenDim; h++)
            vector[h] = hidden[batch, lastIndex, h];
        return vector;
    }

    private static float[] MeanPool(Tensor<float> hidden, int batch, int tokenCount, int hiddenDim)
    {
        var vector = new float[hiddenDim];
        for (var s = 0; s < tokenCount; s++)
        {
            for (var h = 0; h < hiddenDim; h++)
                vector[h] += hidden[batch, s, h];
        }
        for (var h = 0; h < hiddenDim; h++)
            vector[h] /= tokenCount;
        return vector;
    }

    private static DenseTensor<long> BuildLongTensor(int batch, int seq, Func<int, int, long> value)
    {
        var tensor = new DenseTensor<long>([batch, seq]);
        for (var b = 0; b < batch; b++)
            for (var s = 0; s < seq; s++)
                tensor[b, s] = value(b, s);
        return tensor;
    }

    private static void NormalizeInPlace(float[] vector)
    {
        var norm = 0.0;
        foreach (var v in vector)
            norm += v * v;
        norm = Math.Sqrt(norm);
        if (norm <= 0)
            return;
        for (var i = 0; i < vector.Length; i++)
            vector[i] = (float)(vector[i] / norm);
    }

    private static (IOnnxSession Session, IEmbeddingTextEncoder Encoder) CreateRuntime(string modelPath, string tokenizerDir, EmbeddingModelProfile profile)
    {
        IOnnxSession session = new OnnxInferenceSession(modelPath);
        IEmbeddingTextEncoder encoder = profile.TokenizerKind == EmbeddingTokenizerKind.WordPiece
            ? WordPieceTextEncoder.Load(Path.Combine(tokenizerDir, "vocab.txt"))
            : BpeTextEncoder.Load(tokenizerDir);
        return (session, encoder);
    }
}
