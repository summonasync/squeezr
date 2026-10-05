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

public sealed record RefLookupResult(
    string Symbol,
    IReadOnlyList<SymbolRefGroup> Groups,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? GuardWarnings = null);

public sealed class RefLookup
{
    private readonly string _workspaceRoot;

    public RefLookup(string workspaceRoot) => _workspaceRoot = NavigationContext.RequireIndex(workspaceRoot);

    public RefLookupResult Find(string name, string? repo)
    {
        var manifest = WorkspaceManifestFile.Load(_workspaceRoot);
        var guard = SecretGuardReader.Load(_workspaceRoot);
        var blocks = new List<RefBlock>();

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

                var refs = ReadRefs(store, guard, symbol.Id);
                var definition = new RefDefinition(
                    symbol.Kind,
                    declFile?.Path ?? string.Empty,
                    symbol.StartLine,
                    symbol.EndLine);
                var shared = blocks.Find(b => b.Repo == repoDoc.Name
                                              && b.Name == symbol.Name
                                              && b.Refs.SequenceEqual(refs));
                if (shared is null)
                {
                    blocks.Add(new RefBlock(symbol.Name, repoDoc.Name, definition, refs));
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
        return new RefLookupResult(name, groups, guard.Warnings);
    }

    private static List<RefHit> ReadRefs(MetaStore store, SecretGuardReader guard, long symbolId)
    {
        var refs = new List<RefHit>();
        var seen = new HashSet<(string File, int Line, string Kind, string? Enclosing)>();
        foreach (var reference in store.ListRefsByFromSymbol(symbolId).OrderBy(r => r.FileId).ThenBy(r => r.Line))
        {
            var file = store.FindFileById(reference.FileId);
            if (file is not null && guard.TryWithhold(file.Path))
            {
                continue;
            }

            var hit = new RefHit(file?.Path ?? "?", reference.Line, reference.Kind, reference.EnclosingSymbol);
            if (seen.Add((hit.File, hit.Line, hit.Kind, hit.EnclosingSymbol)))
            {
                refs.Add(hit);
            }
        }

        return refs;
    }

    private sealed class RefBlock(string name, string repo, RefDefinition definition, List<RefHit> refs)
    {
        public string Name { get; } = name;

        public string Repo { get; } = repo;

        public List<RefDefinition> Definitions { get; } = [definition];

        public List<RefHit> Refs { get; } = refs;
    }
}
