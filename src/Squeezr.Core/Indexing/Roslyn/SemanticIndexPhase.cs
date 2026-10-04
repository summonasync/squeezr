using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Roslyn;

public sealed record SemanticPhaseResult(
    int Symbols,
    int Chunks,
    int Skipped,
    IReadOnlyList<string>? Deferred = null);

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

        using (session)
        {
            return RunWithSession(
                session, repoRoot, repoDoc, store, csFiles, changedByPath, dbFilesByPath, config, noSemantic, warning, progress);
        }
    }

    public static SemanticPhaseResult RunPooled(
        SemanticSessionPool sessions,
        string repoRoot,
        RepoDocument repoDoc,
        MetaStore store,
        IReadOnlyList<SourceFile> csFiles,
        IReadOnlyDictionary<string, FileDocument> changedByPath,
        IReadOnlyDictionary<string, FileDocument> dbFilesByPath,
        SqueezrConfig config,
        bool noSemantic,
        Action<string> warning,
        IIndexProgress? progress = null,
        bool syncDocuments = false)
    {
        if (csFiles.Count == 0)
        {
            return new SemanticPhaseResult(0, 0, 0);
        }

        var session = sessions.Acquire(
            repoRoot, !noSemantic, out var pooled, ex => warning($"semantic index skipped for repo '{repoDoc.Name}': {ex.Message}"));
        if (session is null)
        {
            // the walk already committed these shas, so nothing would bring them back into scope on its own
            return new SemanticPhaseResult(0, 0, csFiles.Count, csFiles.Select(f => f.RelativePath).ToList());
        }

        try
        {
            if (syncDocuments)
            {
                // the reused session holds the text it opened with: refresh every file this pass hands over
                // before the first parse, so a declaration parsed early in the pass already sees the edits
                // and additions made further down the same batch
                session.SyncContent(csFiles.Select(f => f.RelativePath));
            }

            var result = RunWithSession(
                session, repoRoot, repoDoc, store, csFiles, changedByPath, dbFilesByPath, config,
                noSemantic, warning, progress);

            if (pooled && session.RequiresReopen && sessions.ShouldReopen(repoRoot, session.RejectedPaths))
            {
                sessions.Discard(repoRoot);
            }

            return result;
        }
        finally
        {
            // pooled is ownership: a session the pool keeps must survive this pass, only the one-shot
            // fallback the pool refused to cache belongs to us
            if (!pooled)
            {
                session.Dispose();
            }
        }
    }

    internal static SemanticPhaseResult RunWithSession(
        RoslynSession session,
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
        var skipped = 0;
        var symbolsInserted = 0;
        List<string>? deferred = null;

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
                if (session.HasUnsynced(file.RelativePath))
                {
                    warning($"skipped '{file.RelativePath}': the reused session could not read it; previous data kept");
                    skipped++;
                    (deferred ??= []).Add(file.RelativePath);
                    continue;
                }

                byte[] content;
                try
                {
                    content = File.ReadAllBytes(file.FullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warning($"skipped '{file.RelativePath}': unreadable ({ex.Message})");
                    skipped++;
                    (deferred ??= []).Add(file.RelativePath);
                    continue;
                }

                var warningCountBefore = provider.Warnings.Count;
                var result = provider.Parse(new FileRef(repoDoc.Name, file.RelativePath), content);
                var parseFailed = provider.Warnings.Count > warningCountBefore;
                if (parseFailed)
                {
                    warning($"skipped '{file.RelativePath}': symbol extraction failed; previous data kept");
                    skipped++;
                    (deferred ??= []).Add(file.RelativePath);
                    continue;
                }

                var isChanged = changedByPath.TryGetValue(file.RelativePath, out var fileDoc);
                // under a paced pass only touched files reach this, so drift here means content came back
                // identical over symbols a degraded session wrote. syntax-only signatures never match
                // semantic ones, so that difference is parser noise rather than an edit — keep the rows
                if (!isChanged && session.Semantic
                    && dbFilesByPath.TryGetValue(file.RelativePath, out var existingDoc)
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
                    symbolsForRefs = SymbolPersistence.InsertSymbols(result, fileDoc, repoDoc, store);
                    symbolsByFile[fileDoc.Id] = symbolsForRefs;
                    SymbolPersistence.InsertChunks(result, fileDoc, repoDoc, symbolsForRefs, store);
                    symbolsInserted += symbolsForRefs.Count;
                }
                else if (dbFilesByPath.TryGetValue(file.RelativePath, out var existingDoc2)
                         && symbolsByFile.TryGetValue(existingDoc2.Id, out var existingSymbols2))
                {
                    symbolsForRefs = existingSymbols2;
                }
                else
                {
                    // a parsed file with symbols and no rows on record is a skip somebody committed:
                    // hand it back so the next pass re-runs it instead of trusting the stored sha
                    if (result.Symbols.Count > 0)
                    {
                        (deferred ??= []).Add(file.RelativePath);
                    }

                    continue;
                }

                if (noSemantic)
                {
                    continue;
                }

                // a degraded session resolves no refs; refreshing an unchanged file would
                // delete rows it cannot rebuild, so leave them for the next semantic pass
                if (!isChanged && !session.Semantic)
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

        return new SemanticPhaseResult(store.CountSymbols(repoDoc.Id), store.CountChunks(repoDoc.Id), skipped, deferred);
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
        // chunks are deliberately left alone: MetaStore.ReplaceFileChunks owns them and needs the previous
        // rows alive while it writes the new ones, so a chunk whose text did not move keeps its marker
        if (symbolsByFile.TryGetValue(fileId, out var oldSymbols))
        {
            store.DeleteRefsByFromSymbolIds(oldSymbols.Select(s => s.Id).ToList());
            symbolsByFile.Remove(fileId);
        }

        store.DeleteSymbolsByFile(fileId);
    }

    private static bool HasDrift(List<SymbolDocument> stored, ParseResult result)
    {
        // name + line is not a key any more: a ctor carries its declaring type's name, so a
        // single-line type declaration and its ctor share the pair — compare the whole multiset
        var storedKeys = stored.Select(Key).Order().ToList();
        return !storedKeys.SequenceEqual(result.Symbols.Select(Key).Order());
    }

    private static (string Name, int Line, string? Signature) Key(SymbolDocument symbol) =>
        (symbol.Name, symbol.StartLine, symbol.Signature);

    private static (string Name, int Line, string? Signature) Key(Symbol symbol) =>
        (symbol.Name, symbol.Span.StartLine, symbol.Signature);

    internal static void InsertRefs(
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
                Line = r.Line,
                EnclosingSymbol = r.EnclosingName
            });
        }

        // a linked file backs one document per project, so the same location arrives once per
        // owning project; the enclosing symbol keeps same-line usages distinct (field type
        // annotation vs initializer) from collapsing into one row
        store.InsertRefs(refDocs
            .DistinctBy(r => (r.FromSymbolId, r.FileId, r.Line, r.Kind, r.EnclosingSymbol))
            .ToList());
    }
}
