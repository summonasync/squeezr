using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Navigation;

public sealed record RefHit(string File, int Line, string Kind, string? EnclosingSymbol);

public sealed record SymbolRefGroup(
    string Name,
    string Kind,
    string Repo,
    string File,
    int StartLine,
    int EndLine,
    IReadOnlyList<RefHit> Refs);

public sealed record RefLookupResult(string Symbol, IReadOnlyList<SymbolRefGroup> Groups);

public sealed class RefLookup
{
    private readonly string _workspaceRoot;

    public RefLookup(string workspaceRoot) => _workspaceRoot = NavigationContext.RequireIndex(workspaceRoot);

    public RefLookupResult Find(string name, string? repo)
    {
        var manifest = WorkspaceManifestFile.Load(_workspaceRoot);
        var groups = new List<SymbolRefGroup>();

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
                var refs = store.ListRefsByFromSymbol(symbol.Id)
                    .OrderBy(r => r.FileId)
                    .ThenBy(r => r.Line)
                    .Select(r => new RefHit(store.FindFileById(r.FileId)?.Path ?? "?", r.Line, r.Kind, r.EnclosingSymbol))
                    .ToList();
                groups.Add(new SymbolRefGroup(
                    symbol.Name,
                    symbol.Kind,
                    repoDoc.Name,
                    declFile?.Path ?? string.Empty,
                    symbol.StartLine,
                    symbol.EndLine,
                    refs));
            }
        }

        if (groups.Count == 0)
        {
            throw new WorkspaceException(
                $"No symbol named '{name}' in the index. Run 'squeezr index' if the code changed.");
        }

        return new RefLookupResult(name, groups);
    }
}
