using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Squeezr.Core.Embeddings;

namespace Squeezr.Cli.Embeddings;

public sealed class OnnxEmbeddingProvider : IEmbeddingProvider
{
    private static readonly Regex QwenPreTokenizerRegex = new(
        """(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+""",
        RegexOptions.Compiled);

    private readonly int _maxSequenceLength;
    private readonly int _batchSize;
    private readonly Lazy<(InferenceSession Session, Tokenizer Tokenizer, int EndOfTextId)> _runtime;

    public int Dimensions { get; }

    public OnnxEmbeddingProvider(string modelPath, string tokenizerDir, int dimensions, int maxSequenceLength = 512, int batchSize = 32)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Embedding model not found: {modelPath}", modelPath);
        if (dimensions <= 0)
            throw new ArgumentOutOfRangeException(nameof(dimensions));

        Dimensions = dimensions;
        _maxSequenceLength = maxSequenceLength;
        _batchSize = Math.Max(1, batchSize);
        _runtime = new Lazy<(InferenceSession, Tokenizer, int)>(() => CreateRuntime(modelPath, tokenizerDir));
    }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var (session, tokenizer, endOfTextId) = _runtime.Value;
        var results = new float[texts.Count][];

        for (var batchStart = 0; batchStart < texts.Count; batchStart += _batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batchEnd = Math.Min(batchStart + _batchSize, texts.Count);
            var batchCount = batchEnd - batchStart;

            var tokenIds = new List<int>[batchCount];
            var maxLen = 1;
            for (var i = 0; i < batchCount; i++)
            {
                var ids = tokenizer.EncodeToIds(texts[batchStart + i].Normalize(NormalizationForm.FormC));
                var truncated = ids.Count > _maxSequenceLength ? ids.Take(_maxSequenceLength).ToList() : ids.ToList();
                tokenIds[i] = truncated.Count == 0 ? [endOfTextId] : truncated;
                maxLen = Math.Max(maxLen, tokenIds[i].Count);
            }

            var inputNames = session.InputMetadata.Keys.ToList();
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
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, BuildLongTensor(batchCount, maxLen, (b, s) => s < tokenIds[b].Count ? tokenIds[b][s] : endOfTextId)));
            }

            using var output = session.Run(inputs);
            var hidden = output[0].AsTensor<float>() ?? throw new InvalidOperationException("The embedding model returned no tensor output.");
            if (hidden.Rank != 3)
                throw new InvalidOperationException($"Expected a [batch, sequence, hidden] output, got rank {hidden.Rank}.");

            var hiddenDim = hidden.Dimensions[2];
            for (var b = 0; b < batchCount; b++)
            {
                var lastIndex = tokenIds[b].Count - 1;
                var vector = new float[hiddenDim];
                for (var h = 0; h < hiddenDim; h++)
                    vector[h] = hidden[b, lastIndex, h];
                NormalizeInPlace(vector);
                results[batchStart + b] = vector;
            }
        }

        return Task.FromResult(results);
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

    private static (InferenceSession Session, Tokenizer Tokenizer, int EndOfTextId) CreateRuntime(string modelPath, string tokenizerDir)
    {
        var vocabPath = Path.Combine(tokenizerDir, "vocab.json");
        var mergesPath = Path.Combine(tokenizerDir, "merges.txt");
        if (!File.Exists(vocabPath) || !File.Exists(mergesPath))
            throw new FileNotFoundException($"Tokenizer files not found in '{tokenizerDir}' (expected vocab.json and merges.txt).");

        var addedTokensPath = Path.Combine(tokenizerDir, "added_tokens.json");
        var specialTokens = File.Exists(addedTokensPath)
            ? JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(addedTokensPath)) ?? new Dictionary<string, int>()
            : new Dictionary<string, int>();

        var options = new BpeOptions(vocabPath, mergesPath)
        {
            PreTokenizer = new RegexPreTokenizer(QwenPreTokenizerRegex, specialTokens),
            SpecialTokens = specialTokens,
            ByteLevel = true,
        };
        Tokenizer tokenizer = BpeTokenizer.Create(options);

        var endOfTextId = specialTokens.TryGetValue("<|endoftext|>", out var id) ? id : 0;
        var session = new InferenceSession(modelPath);
        return (session, tokenizer, endOfTextId);
    }
}
