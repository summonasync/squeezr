using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Roslyn;

public sealed record SemanticPhaseResult(int Symbols, int Chunks, int Skipped);

public static class SemanticIndexPhase
{
    public static SemanticPhaseResult Run(
        string repoRoot,
        RepoDocument repoDoc,
        MetaStore store,
        IReadOnlyList<SourceFile> csFiles,
        IReadOnlyDictionary<string, FileDocument> changedByPath,
        IReadOnlyDictionary<string, FileDocument> dbFilesByPath,
        SqueezrConfig config,
        bool noSemantic,
        Action<string> warning,
        IIndexProgress? progress = null)
    {
        if (csFiles.Count == 0)
        {
            return new SemanticPhaseResult(0, 0, 0);
        }

        RoslynSession session;
        try
        {
            session = RoslynSession.Create(repoRoot, semantic: !noSemantic);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException and not OperationCanceledException)
        {
            warning($"semantic index skipped for repo '{repoDoc.Name}': {ex.Message}");
            return new SemanticPhaseResult(0, 0, 0);
        }

        var skipped = 0;
        var symbolsInserted = 0;
        using (session)
        {
            progress?.PhaseStarted(repoDoc.Name, IndexPhase.Semantic, csFiles.Count);

            foreach (var degradation in session.Warnings.Where(IsDegradationWarning))
            {
                warning(degradation);
            }

            var provider = new RoslynProvider(session, config);
            var symbolsByFile = store.ListSymbolsByRepo(repoDoc.Id)
                .GroupBy(s => s.FileId)
                .ToDictionary(g => g.Key, g => g.ToList());

            if (noSemantic)
            {
                PurgeRefs(csFiles, dbFilesByPath, symbolsByFile, store);
            }

            foreach (var file in csFiles)
            {
                try
                {
                    byte[] content;
                    try
                    {
                        content = File.ReadAllBytes(file.FullPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warning($"skipped '{file.RelativePath}': unreadable ({ex.Message})");
                        skipped++;
                        continue;
                    }

                    var warningCountBefore = provider.Warnings.Count;
                    var result = provider.Parse(new FileRef(repoDoc.Name, file.RelativePath), content);
                    var parseFailed = provider.Warnings.Count > warningCountBefore;
                    if (parseFailed)
                    {
                        warning($"skipped '{file.RelativePath}': symbol extraction failed; previous data kept");
                        skipped++;
                        continue;
                    }

                    var isChanged = changedByPath.TryGetValue(file.RelativePath, out var fileDoc);
                    if (!isChanged && dbFilesByPath.TryGetValue(file.RelativePath, out var existingDoc)
                        && symbolsByFile.TryGetValue(existingDoc.Id, out var existingSymbols)
                        && HasDrift(existingSymbols, result))
                    {
                        isChanged = true;
                        fileDoc = existingDoc;
                    }

                    List<SymbolDocument> symbolsForRefs;
                    if (isChanged)
                    {
                        CascadeFile(fileDoc!.Id, symbolsByFile, store);
                        symbolsForRefs = InsertSymbols(result, fileDoc, repoDoc, store);
                        symbolsByFile[fileDoc.Id] = symbolsForRefs;
                        InsertChunks(result, fileDoc, repoDoc, symbolsForRefs, store);
                        symbolsInserted += symbolsForRefs.Count;
                    }
                    else if (dbFilesByPath.TryGetValue(file.RelativePath, out var existingDoc2)
                             && symbolsByFile.TryGetValue(existingDoc2.Id, out var existingSymbols2))
                    {
                        symbolsForRefs = existingSymbols2;
                    }
                    else
                    {
                        continue;
                    }

                    if (noSemantic)
                    {
                        continue;
                    }

                    if (!isChanged)
                    {
                        store.DeleteRefsByFromSymbolIds(symbolsForRefs.Select(s => s.Id).ToList());
                    }

                    InsertRefs(result, symbolsForRefs, repoRoot, dbFilesByPath, store);
                }
                finally
                {
                    progress?.ItemProcessed(repoDoc.Name, file.RelativePath, symbolsInserted);
                }
            }

            foreach (var providerWarning in provider.Warnings)
            {
                if (providerWarning.StartsWith("parse failed", StringComparison.OrdinalIgnoreCase))
                {
                    warning(providerWarning);
                }
            }
        }

        return new SemanticPhaseResult(store.CountSymbols(repoDoc.Id), store.CountChunks(repoDoc.Id), skipped);
    }

    private static bool IsDegradationWarning(string sessionWarning) =>
        sessionWarning.StartsWith("MSBuild workspace unavailable", StringComparison.OrdinalIgnoreCase)
        || sessionWarning.StartsWith("no reference assemblies on disk", StringComparison.OrdinalIgnoreCase);

    private static void PurgeRefs(
        IReadOnlyList<SourceFile> csFiles,
        IReadOnlyDictionary<string, FileDocument> dbFilesByPath,
        IReadOnlyDictionary<uint, List<SymbolDocument>> symbolsByFile,
        MetaStore store)
    {
        var allIds = new List<long>();
        foreach (var file in csFiles)
        {
            if (dbFilesByPath.TryGetValue(file.RelativePath, out var doc)
                && symbolsByFile.TryGetValue(doc.Id, out var symbols))
            {
                allIds.AddRange(symbols.Select(s => s.Id));
            }
        }

        store.DeleteRefsByFromSymbolIds(allIds);
    }

    private static void CascadeFile(
        uint fileId,
        Dictionary<uint, List<SymbolDocument>> symbolsByFile,
        MetaStore store)
    {
        if (symbolsByFile.TryGetValue(fileId, out var oldSymbols))
        {
            store.DeleteRefsByFromSymbolIds(oldSymbols.Select(s => s.Id).ToList());
            symbolsByFile.Remove(fileId);
        }

        store.DeleteSymbolsByFile(fileId);
        store.DeleteChunksByFile(fileId);
    }

    private static bool HasDrift(List<SymbolDocument> stored, ParseResult result)
    {
        var storedByKey = stored.ToDictionary(s => (s.Name, s.StartLine), s => s.Signature);
        if (storedByKey.Count != result.Symbols.Count)
        {
            return true;
        }

        foreach (var symbol in result.Symbols)
        {
            if (!storedByKey.TryGetValue((symbol.Name, symbol.Span.StartLine), out var signature)
                || signature != symbol.Signature)
            {
                return true;
            }
        }

        return false;
    }

    private static List<SymbolDocument> InsertSymbols(
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

    private static void InsertChunks(
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

    private static void InsertRefs(
        ParseResult result,
        List<SymbolDocument> symbolsForRefs,
        string repoRoot,
        IReadOnlyDictionary<string, FileDocument> dbFilesByPath,
        MetaStore store)
    {
        var refDocs = new List<RefDocument>();
        foreach (var r in result.Refs)
        {
            var target = symbolsForRefs.FirstOrDefault(s => s.Signature is not null && s.Signature == r.ToName);
            if (target is null || r.FromPath is null)
            {
                continue;
            }

            var relativeFrom = Path.GetRelativePath(repoRoot, r.FromPath).Replace('\\', '/');
            if (!dbFilesByPath.TryGetValue(relativeFrom, out var fromDoc))
            {
                continue;
            }

            refDocs.Add(new RefDocument
            {
                FromSymbolId = target.Id,
                ToSymbolId = r.ToSymbolId,
                ToName = r.ToName ?? string.Empty,
                Kind = r.Kind.ToString().ToLowerInvariant(),
                FileId = fromDoc.Id,
                Line = r.Line
            });
        }

        store.InsertRefs(refDocs);
    }
}
