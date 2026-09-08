using System.Security.Cryptography;
using System.Text;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Core.Indexing.TreeSitter;

internal static class TsSymbolChunkBuilder
{
    public static IReadOnlyList<CodeChunk> Build(IReadOnlyList<Symbol> symbols, string text, ChunkingConfig config)
    {
        var lines = text.Split('\n');
        var chunks = new List<CodeChunk>();
        foreach (var symbol in symbols)
        {
            var start = symbol.Span.StartLine;
            var end = Math.Min(symbol.Span.EndLine, lines.Length);
            if (start < 1 || start > end)
            {
                continue;
            }

            var body = string.Join("\n", lines[(start - 1)..end]);
            if (TokenCounter.Estimate(body) <= config.MaxTokensPerChunk)
            {
                chunks.Add(Create(symbol.Span, body));
                continue;
            }

            Split(symbol, lines, start, end, config.MaxTokensPerChunk, chunks);
        }

        return chunks;
    }

    private static void Split(Symbol symbol, string[] lines, int start, int end, int maxTokens, List<CodeChunk> chunks)
    {
        var prefix = (symbol.Signature ?? string.Empty) + "\n";
        var budget = Math.Max(8, maxTokens - TokenCounter.Estimate(prefix));

        var groupStart = start;
        var current = new StringBuilder();
        for (var line = start; line <= end; line++)
        {
            var lineText = lines[line - 1];
            var candidate = current.Length == 0 ? lineText : current + "\n" + lineText;
            if (TokenCounter.Estimate(candidate) > budget && current.Length > 0)
            {
                chunks.Add(Create(new CodeSpan(groupStart, line - 1), prefix + current));
                groupStart = line;
                current.Clear();
            }

            current.Append(current.Length == 0 ? lineText : "\n" + lineText);
        }

        if (current.Length > 0)
        {
            chunks.Add(Create(new CodeSpan(groupStart, end), prefix + current));
        }
    }

    private static CodeChunk Create(CodeSpan span, string text) =>
        new(null, span, text, TokenCounter.Estimate(text),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
}
