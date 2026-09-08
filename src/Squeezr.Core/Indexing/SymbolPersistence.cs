using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing;

internal static class SymbolPersistence
{
    public static List<SymbolDocument> InsertSymbols(
        ParseResult result, FileDocument fileDoc, RepoDocument repoDoc, MetaStore store)
    {
        var symbolDocs = result.Symbols.Select(s => new SymbolDocument
        {
            FileId = fileDoc.Id,
            RepoId = repoDoc.Id,
            Name = s.Name,
            Kind = s.Kind.ToString().ToLowerInvariant(),
            Signature = s.Signature,
            StartLine = s.Span.StartLine,
            EndLine = s.Span.EndLine,
            ContainerName = s.ContainerName,
            DocComment = s.DocComment
        }).ToList();
        store.InsertSymbols(symbolDocs);
        return symbolDocs;
    }

    public static void InsertChunks(
        ParseResult result,
        FileDocument fileDoc,
        RepoDocument repoDoc,
        List<SymbolDocument> symbolDocs,
        MetaStore store)
    {
        var chunkDocs = result.Chunks.Select(c => new ChunkDocument
        {
            FileId = fileDoc.Id,
            RepoId = repoDoc.Id,
            SymbolId = FindContainingSymbol(c.Span, symbolDocs),
            StartLine = c.Span.StartLine,
            EndLine = c.Span.EndLine,
            Text = c.Text,
            TokenCount = c.TokenCount,
            ChunkHash = c.ChunkHash
        }).ToList();
        store.InsertChunks(chunkDocs);
    }

    private static long? FindContainingSymbol(CodeSpan span, List<SymbolDocument> symbols) =>
        symbols.Where(s => s.StartLine <= span.StartLine && s.EndLine >= span.EndLine)
            .OrderBy(s => s.EndLine - s.StartLine)
            .Select(s => (long?)s.Id)
            .FirstOrDefault();
}
