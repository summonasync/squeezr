using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

namespace Squeezr.Cli.Embeddings;

internal sealed class BpeTextEncoder : IEmbeddingTextEncoder
{
    private static readonly Regex QwenPreTokenizerRegex = new(
        """(?i:'s|'t|'re|'ve|'m|'ll|'d)|[^\r\n\p{L}\p{N}]?\p{L}+|\p{N}| ?[^\s\p{L}\p{N}]+[\r\n]*|\s*[\r\n]+|\s+(?!\S)|\s+""",
        RegexOptions.Compiled);

    private readonly Tokenizer _tokenizer;

    private BpeTextEncoder(Tokenizer tokenizer, int fallbackTokenId)
    {
        _tokenizer = tokenizer;
        FallbackTokenId = fallbackTokenId;
    }

    public int FallbackTokenId { get; }

    public IReadOnlyList<int> EncodeToIds(string text) => _tokenizer.EncodeToIds(text);

    public static BpeTextEncoder Load(string tokenizerDir)
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

        var fallbackTokenId = specialTokens.TryGetValue("<|endoftext|>", out var id) ? id : 0;
        return new BpeTextEncoder(tokenizer, fallbackTokenId);
    }
}
