using System.Text.Json.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Navigation;

public sealed record RefHit(string File, int Line, string Kind, string? EnclosingSymbol);

public sealed record RefDefinition(string Kind, string File, int StartLine, int EndLine);

// one block per distinct reference set: same-name definitions that resolve to the very same
// callsites share a block and only contribute their declaration spans
public sealed record SymbolRefGroup(
    string Name,
    string Repo,
    IReadOnlyList<RefDefinition> Definitions,
    IReadOnlyList<RefHit> Refs);

// a declaring file whose rows predate a caller's newest index pass: rows are written when the
// declaring file is parsed, so until it is parsed again a callsite removed in that caller can still
// show and one added in it is missing
public sealed record GhostRefDeclaration(string Repo, string File, int LaggingRows);

// the same predicate measured over the whole store: declaring files still owing a re-parse,
// the rows that prove it, and the rows scanned — a count without its denominator is not a measurement
public sealed record GhostRefLag(int OverdueDeclarations, int LaggingRows, int ScannedRows)
{
    public bool HasLag => OverdueDeclarations > 0;
}

public sealed record RefLookupResult(
    string Symbol,
    IReadOnlyList<SymbolRefGroup> Groups,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? GuardWarnings = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<GhostRefDeclaration>? LaggingDeclarations = null);

public sealed class RefLookup
{
    private readonly string _workspaceRoot;

    public RefLookup(string workspaceRoot) => _workspaceRoot = NavigationContext.RequireIndex(workspaceRoot);

    public RefLookupResult Find(string name, string? repo)
    {
        var manifest = WorkspaceManifestFile.Load(_workspaceRoot);
        var guard = SecretGuardReader.Load(_workspaceRoot);
        var blocks = new List<RefBlock>();
        var lagging = new List<GhostRefDeclaration>();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(_workspaceRoot));
        foreach (var entry in NavigationContext.SelectRepos(manifest, repo))
        {
            var repoDoc = store.FindRepo(entry.Name);
            if (repoDoc is null)
            {
                continue;
            }

            var matches = store.ListSymbolsByRepo(repoDoc.Id)
                .Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.FileId)
                .ThenBy(s => s.StartLine);
            foreach (var symbol in matches)
            {
                var declFile = store.FindFileById(symbol.FileId);
                if (declFile is not null && guard.TryWithhold(declFile.Path))
                {
                    continue;
                }

                var refs = ReadRefs(store, guard, symbol.Id, declFile);
                if (refs.LaggingRows > 0 && declFile is not null)
                {
                    AddLagging(lagging, repoDoc.Name, declFile.Path, refs.LaggingRows);
                }

                var definition = new RefDefinition(
                    symbol.Kind,
                    declFile?.Path ?? string.Empty,
                    symbol.StartLine,
                    symbol.EndLine);
                var shared = blocks.Find(b => b.Repo == repoDoc.Name
                                              && b.Name == symbol.Name
                                              && b.Refs.SequenceEqual(refs.Hits));
                if (shared is null)
                {
                    blocks.Add(new RefBlock(symbol.Name, repoDoc.Name, definition, refs.Hits));
                }
                else
                {
                    shared.Definitions.Add(definition);
                }
            }
        }

        if (blocks.Count == 0)
        {
            throw guard.FirstWithheldPath is { } hidden
                ? guard.Refusal(hidden)
                : new WorkspaceException(
                    $"No symbol named '{name}' in the index. Run 'squeezr index' if the code changed.");
        }

        var groups = blocks
            .Select(b => new SymbolRefGroup(b.Name, b.Repo, b.Definitions, b.Refs))
            .ToList();
        return new RefLookupResult(name, groups, guard.Warnings, lagging.Count > 0 ? lagging : null);
    }

    private static (List<RefHit> Hits, int LaggingRows) ReadRefs(
        MetaStore store,
        SecretGuardReader guard,
        long symbolId,
        FileDocument? declaring)
    {
        var hits = new List<RefHit>();
        var seen = new HashSet<(string File, int Line, string Kind, string? Enclosing)>();
        var laggingRows = 0;
        foreach (var reference in store.ListRefsByFromSymbol(symbolId).OrderBy(r => r.FileId).ThenBy(r => r.Line))
        {
            var file = store.FindFileById(reference.FileId);
            if (file is not null && guard.TryWithhold(file.Path))
            {
                continue;
            }

            // the row was written when the declaring side was parsed, and the caller's text is only
            // reconciled against it when that happens again — a newer caller stamp means this row
            // describes a callsite the caller has moved past, and calls it added are still missing
            if (declaring is not null && file is not null && file.IndexedAt > declaring.IndexedAt)
            {
                laggingRows++;
            }

            var hit = new RefHit(file?.Path ?? "?", reference.Line, reference.Kind, reference.EnclosingSymbol);
            if (seen.Add((hit.File, hit.Line, hit.Kind, hit.EnclosingSymbol)))
            {
                hits.Add(hit);
            }
        }

        return (hits, laggingRows);
    }

    private static void AddLagging(List<GhostRefDeclaration> lagging, string repo, string file, int rows)
    {
        for (var i = 0; i < lagging.Count; i++)
        {
            if (lagging[i].Repo != repo || lagging[i].File != file)
            {
                continue;
            }

            lagging[i] = lagging[i] with { LaggingRows = lagging[i].LaggingRows + rows };
            return;
        }

        lagging.Add(new GhostRefDeclaration(repo, file, rows));
    }

    // the whole-store reading of the same predicate: how many declaring files currently carry rows
    // that a later caller pass has moved past. index_status asks once per call, not per row.
    public static GhostRefLag MeasureLag(MetaStore store)
    {
        var stamps = new Dictionary<uint, DateTime>();
        var declaringBySymbol = new Dictionary<long, uint>();
        foreach (var repo in store.ListRepos())
        {
            foreach (var file in store.ListFiles(repo.Id))
            {
                stamps[file.Id] = file.IndexedAt;
            }

            foreach (var symbol in store.ListSymbolsByRepo(repo.Id))
            {
                declaringBySymbol[symbol.Id] = symbol.FileId;
            }
        }

        var overdue = new HashSet<uint>();
        var laggingRows = 0;
        var scannedRows = 0;
        foreach (var reference in store.ListRefEndpoints())
        {
            scannedRows++;
            if (!declaringBySymbol.TryGetValue(reference.FromSymbolId, out var declaringId)
                || !stamps.TryGetValue(declaringId, out var declaringAt)
                || !stamps.TryGetValue(reference.FileId, out var callerAt))
            {
                continue;
            }

            if (callerAt > declaringAt)
            {
                overdue.Add(declaringId);
                laggingRows++;
            }
        }

        return new GhostRefLag(overdue.Count, laggingRows, scannedRows);
    }

    private sealed class RefBlock(string name, string repo, RefDefinition definition, List<RefHit> refs)
    {
        public string Name { get; } = name;

        public string Repo { get; } = repo;

        public List<RefDefinition> Definitions { get; } = [definition];

        public List<RefHit> Refs { get; } = refs;
    }
}
