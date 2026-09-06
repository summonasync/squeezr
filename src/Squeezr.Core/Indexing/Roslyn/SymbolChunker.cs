using System.Security.Cryptography;
using System.Text;
using Microsoft.ML.Tokenizers;
using Squeezr.Core.Configuration;

namespace Squeezr.Core.Indexing.Roslyn;

public static class SymbolChunker
{
    private static readonly Tokenizer Tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

    public static int CountTokens(string text) => Tokenizer.CountTokens(text);

    public static IReadOnlyList<CodeChunk> Build(IReadOnlyList<ExtractedSymbol> symbols, string text, ChunkingConfig config)
    {
        var lines = text.Split('\n');
        var chunks = new List<CodeChunk>();
        foreach (var symbol in symbols)
        {
            var start = symbol.Symbol.Span.StartLine;
            var end = Math.Min(symbol.Symbol.Span.EndLine, lines.Length);
            if (start < 1 || start > end)
            {
                continue;
            }

            var body = string.Join("\n", lines[(start - 1)..end]);
            if (CountTokens(body) <= config.MaxTokensPerChunk)
            {
                chunks.Add(Create(null, symbol.Symbol.Span, body));
                continue;
            }

            Split(symbol, lines, start, end, config.MaxTokensPerChunk, chunks);
        }

        return chunks;
    }

    private static void Split(
        ExtractedSymbol symbol,
        string[] lines,
        int start,
        int end,
        int maxTokens,
        List<CodeChunk> chunks)
    {
        var prefix = symbol.Signature + "\n";
        var prefixTokens = CountTokens(prefix);
        var budget = Math.Max(8, maxTokens - prefixTokens);

        var groupStart = start;
        var current = new StringBuilder();
        for (var line = start; line <= end; line++)
        {
            var lineText = lines[line - 1];
            var candidate = current.Length == 0 ? lineText : current + "\n" + lineText;
            if (CountTokens(candidate) > budget && current.Length > 0)
            {
                chunks.Add(Create(null, new CodeSpan(groupStart, line - 1), prefix + current));
                groupStart = line;
                current.Clear();
            }

            current.Append(current.Length == 0 ? lineText : "\n" + lineText);
        }

        if (current.Length > 0)
        {
            chunks.Add(Create(null, new CodeSpan(groupStart, end), prefix + current));
        }
    }

    private static CodeChunk Create(string? symbolId, CodeSpan span, string text) =>
        new(symbolId, span, text, CountTokens(text), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
}
