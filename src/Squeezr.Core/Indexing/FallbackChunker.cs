using System.Security.Cryptography;
using System.Text;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing;

public static class FallbackChunker
{
    public static IReadOnlyList<CodeChunk> Chunk(string content, ChunkingConfig config)
    {
        var lines = content.Split('\n');
        var window = Math.Max(1, config.FallbackWindowLines);
        var overlap = Math.Clamp(config.FallbackOverlapLines, 0, window - 1);
        var step = Math.Max(1, window - overlap);
        var maxTokens = Math.Max(1, config.MaxTokensPerChunk);

        var chunks = new List<CodeChunk>();
        for (var start = 1; start <= lines.Length; start += step)
        {
            var end = Math.Min(start + window - 1, lines.Length);
            Split(lines, start, end, maxTokens, chunks);
            if (end == lines.Length)
            {
                break;
            }
        }

        return chunks;
    }

    internal static void Persist(
        MetaStore store, RepoDocument repoDoc, FileDocument doc, string text, ChunkingConfig chunking)
    {
        store.DeleteChunksByFile(doc.Id);
        var chunks = Chunk(text, chunking);
        if (chunks.Count == 0)
        {
            return;
        }

        store.InsertChunks(chunks.Select(c => new ChunkDocument
        {
            FileId = doc.Id,
            RepoId = repoDoc.Id,
            StartLine = c.Span.StartLine,
            EndLine = c.Span.EndLine,
            Text = c.Text,
            TokenCount = c.TokenCount,
            ChunkHash = c.ChunkHash,
        }).ToList());
    }

    private static void Split(string[] lines, int start, int end, int maxTokens, List<CodeChunk> chunks)
    {
        var groupStart = start;
        var current = new StringBuilder();
        for (var line = start; line <= end; line++)
        {
            var candidate = current.Length == 0 ? lines[line - 1] : current + "\n" + lines[line - 1];
            if (current.Length > 0 && TokenCounter.Estimate(candidate) > maxTokens)
            {
                Flush(groupStart, line - 1, current, chunks);
                groupStart = line;
                current.Clear();
                current.Append(lines[line - 1]);
                continue;
            }

            current.Clear();
            current.Append(candidate);
        }

        if (current.Length > 0 || groupStart <= end)
        {
            Flush(groupStart, end, current, chunks);
        }
    }

    private static void Flush(int start, int end, StringBuilder text, List<CodeChunk> chunks)
    {
        var body = text.ToString();
        chunks.Add(new CodeChunk(
            null,
            new CodeSpan(start, end),
            body,
            TokenCounter.Estimate(body),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))));
    }
}
