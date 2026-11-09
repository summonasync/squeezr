using System.Text;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.TreeSitter;

public sealed record TreeSitterPhaseResult(int Symbols, int Chunks, int Skipped, IReadOnlyList<string> FailedPaths);

public static class TreeSitterIndexPhase
{
    public static TreeSitterPhaseResult Run(
        string repoRoot,
        RepoDocument repoDoc,
        MetaStore store,
        IReadOnlyList<FileDocument> targetFiles,
        Dictionary<uint, string> textCache,
        ChunkingConfig chunking,
        bool noSemantic,
        IParsingProviderWithWarnings provider,
        Action<string> warning,
        IIndexProgress? progress = null)
    {
        var failed = new List<string>();
        if (targetFiles.Count == 0)
        {
            return new TreeSitterPhaseResult(0, 0, 0, failed);
        }

        var tsFiles = store.ListFiles(repoDoc.Id).Where(f => IsTreeSitter(f.Language)).ToList();
        if (noSemantic)
        {
            foreach (var doc in targetFiles)
            {
                CascadeFile(doc.Id, store);
                if (textCache.TryGetValue(doc.Id, out var text))
                {
                    FallbackChunker.Persist(store, repoDoc, doc, text, chunking);
                }
            }

            store.DeleteRefsByFileIds(tsFiles.Select(f => f.Id).ToList());
            return new TreeSitterPhaseResult(0, 0, 0, failed);
        }

        var parsedByPath = new Dictionary<string, ParseResult>(StringComparer.Ordinal);
        var failedPaths = new HashSet<string>(StringComparer.Ordinal);
        var symbolsInserted = 0;
        var skipped = 0;
        progress?.PhaseStarted(repoDoc.Name, IndexPhase.Semantic, targetFiles.Count);

        foreach (var doc in targetFiles)
        {
            try
            {
                if (!textCache.TryGetValue(doc.Id, out var text))
                {
                    skipped++;
                    continue;
                }

                var warningsBefore = provider.Warnings.Count;
                var result = provider.Parse(new FileRef(repoDoc.Name, doc.Path), Encoding.UTF8.GetBytes(text));
                if (provider.Warnings.Count > warningsBefore)
                {
                    failed.Add(doc.Path);
                    failedPaths.Add(doc.Path);
                    skipped++;
                    CascadeFile(doc.Id, store);
                    continue;
                }

                CascadeFile(doc.Id, store);
                var symbolDocs = SymbolPersistence.InsertSymbols(result, doc, repoDoc, store);
                SymbolPersistence.InsertChunks(result, doc, repoDoc, symbolDocs, store);
                parsedByPath[doc.Path] = result;
                symbolsInserted += symbolDocs.Count;
            }
            finally
            {
                progress?.ItemProcessed(repoDoc.Name, doc.Path, symbolsInserted);
            }
        }

        RebuildRefs(repoRoot, repoDoc, store, tsFiles, parsedByPath, failedPaths, textCache, provider);

        if (failed.Count > 0)
        {
            warning(
                $"tree-sitter failed for {failed.Count} file(s), fell back to sliding-window chunks: " +
                string.Join(", ", failed));
        }

        return new TreeSitterPhaseResult(symbolsInserted, store.CountChunks(repoDoc.Id), skipped, failed);
    }

    private static void RebuildRefs(
        string repoRoot,
        RepoDocument repoDoc,
        MetaStore store,
        List<FileDocument> tsFiles,
        Dictionary<string, ParseResult> parsedByPath,
        HashSet<string> failedPaths,
        Dictionary<uint, string> textCache,
        IParsingProviderWithWarnings provider)
    {
        var tsFileIds = tsFiles.Select(f => f.Id).ToHashSet();
        var tsSymbols = store.ListSymbolsByRepo(repoDoc.Id)
            .Where(s => tsFileIds.Contains(s.FileId))
            .ToList();
        store.DeleteRefsByFileIds(tsFiles.Select(f => f.Id).ToList());

        foreach (var file in tsFiles)
        {
            if (failedPaths.Contains(file.Path))
            {
                continue;
            }

            if (!parsedByPath.TryGetValue(file.Path, out var result))
            {
                if (!TryReadText(repoRoot, file, textCache, out var text))
                {
                    continue;
                }

                var warningsBefore = provider.Warnings.Count;
                result = provider.Parse(new FileRef(repoDoc.Name, file.Path), Encoding.UTF8.GetBytes(text));
                if (provider.Warnings.Count > warningsBefore)
                {
                    continue;
                }
            }

            InsertRefs(result, tsSymbols, file, store);
        }
    }

    private static bool TryReadText(
        string repoRoot, FileDocument file, Dictionary<uint, string> textCache, out string text)
    {
        if (textCache.TryGetValue(file.Id, out text!))
        {
            return true;
        }

        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(repoRoot, file.Path.Replace('/', Path.DirectorySeparatorChar)));
            text = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
                : Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool IsTreeSitter(string language) =>
        language is "javascript" or "typescript" or "tsx" or "rust";

    private static void InsertRefs(
        ParseResult result,
        IReadOnlyList<SymbolDocument> repoSymbols,
        FileDocument fileDoc,
        MetaStore store)
    {
        var refDocs = new List<RefDocument>();
        foreach (var r in result.Refs)
        {
            var matches = repoSymbols
                .Where(s => string.Equals(s.Name, r.ToName, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0)
            {
                refDocs.Add(CreateRef(r, 0, fileDoc));
                continue;
            }

            refDocs.AddRange(matches.Select(m => CreateRef(r, m.Id, fileDoc)));
        }

        store.InsertRefs(refDocs);
    }

    private static RefDocument CreateRef(SymbolRef r, long fromSymbolId, FileDocument fileDoc) => new()
    {
        FromSymbolId = fromSymbolId,
        ToSymbolId = r.ToSymbolId,
        ToName = r.ToName ?? string.Empty,
        Kind = r.Kind.ToString().ToLowerInvariant(),
        FileId = fileDoc.Id,
        Line = r.Line,
        EnclosingSymbol = r.EnclosingName
    };

    private static void CascadeFile(uint fileId, MetaStore store)
    {
        var oldIds = store.ListSymbolsByFile(fileId).Select(s => s.Id).ToList();
        store.DeleteRefsByFromSymbolIds(oldIds);
        store.DeleteRefsByFile(fileId);
        store.DeleteSymbolsByFile(fileId);
    }
}
