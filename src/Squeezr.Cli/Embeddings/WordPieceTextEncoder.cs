using Microsoft.ML.Tokenizers;

namespace Squeezr.Cli.Embeddings;

internal sealed class WordPieceTextEncoder : IEmbeddingTextEncoder
{
    private readonly Tokenizer _tokenizer;
    private readonly int? _clsTokenId;
    private readonly int? _sepTokenId;

    private WordPieceTextEncoder(Tokenizer tokenizer, int fallbackTokenId, int? clsTokenId, int? sepTokenId)
    {
        _tokenizer = tokenizer;
        FallbackTokenId = fallbackTokenId;
        _clsTokenId = clsTokenId;
        _sepTokenId = sepTokenId;
    }

    public int FallbackTokenId { get; }

    public IReadOnlyList<int> EncodeToIds(string text)
    {
        var ids = _tokenizer.EncodeToIds(text);
        if (_clsTokenId is null || _sepTokenId is null)
            return ids;

        var wrapped = new List<int>(ids.Count + 2) { _clsTokenId.Value };
        wrapped.AddRange(ids);
        wrapped.Add(_sepTokenId.Value);
        return wrapped;
    }

    public static WordPieceTextEncoder Load(string vocabPath)
    {
        if (!File.Exists(vocabPath))
            throw new FileNotFoundException($"WordPiece vocab file not found: {vocabPath}", vocabPath);

        var options = new WordPieceOptions
        {
            Normalizer = LowerCaseNormalizer.Instance,
        };
        WordPieceTokenizer tokenizer = WordPieceTokenizer.Create(vocabPath, options);
        var (clsTokenId, sepTokenId) = ResolveBoundaryTokenIds(vocabPath);
        return new WordPieceTextEncoder(tokenizer, tokenizer.UnknownTokenId, clsTokenId, sepTokenId);
    }

    private static (int? ClsTokenId, int? SepTokenId) ResolveBoundaryTokenIds(string vocabPath)
    {
        int? clsTokenId = null;
        int? sepTokenId = null;
        var index = 0;
        foreach (var line in File.ReadLines(vocabPath))
        {
            if (line == "[CLS]")
                clsTokenId = index;
            else if (line == "[SEP]")
                sepTokenId = index;
            index++;
        }

        return (clsTokenId, sepTokenId);
    }
}
