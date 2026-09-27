using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Navigation;

public sealed record SymbolHit(
    string Repo,
    string File,
    int StartLine,
    int EndLine,
    string Name,
    string Kind,
    string? Signature,
    string? ContainerName,
    string? DocComment,
    string? Body,
    bool BodyOmitted);

public sealed record SymbolLookupResult(
    IReadOnlyList<SymbolHit> Matches,
    int TokensUsed,
    bool Truncated,
    TruncationHint? Hint);

public sealed class SymbolLookup
{
    public const int DefaultMaxTokens = 300;

    private static readonly TruncationHint Truncation = new(
    [
        new("narrow the query", ["repo", "kind"]),
        new("raise", ["maxTokens"])
    ]);

    private readonly string _workspaceRoot;

    public SymbolLookup(string workspaceRoot) => _workspaceRoot = NavigationContext.RequireIndex(workspaceRoot);

    public SymbolLookupResult Find(string name, string? repo, string? kind, int maxTokens, bool body)
    {
        var manifest = WorkspaceManifestFile.Load(_workspaceRoot);
        var budget = new TokenBudget(maxTokens);
        var hits = new List<SymbolHit>();

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(_workspaceRoot));
        var all = new List<(RepoEntry Entry, RepoDocument Repo, SymbolDocument Symbol)>();
        foreach (var entry in NavigationContext.SelectRepos(manifest, repo))
        {
            var repoDoc = store.FindRepo(entry.Name);
            if (repoDoc is null)
            {
                continue;
            }

            var matches = store.ListSymbolsByRepo(repoDoc.Id)
                .Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                .Where(s => kind is null || s.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.FileId)
                .ThenBy(s => s.StartLine);
            foreach (var symbol in matches)
            {
                all.Add((entry, repoDoc, symbol));
            }
        }

        var includeBody = body || all.Count == 1;
        foreach (var (entry, repoDoc, symbol) in all)
        {
            if (!TryRender(symbol, entry, repoDoc, store, budget, includeBody, hits))
            {
                return new SymbolLookupResult(hits, budget.Used, true, Truncation);
            }
        }

        if (hits.Count == 0)
        {
            throw new WorkspaceException(
                $"No symbol named '{name}' in the index. Run 'squeezr index' if the code changed.");
        }

        return new SymbolLookupResult(hits, budget.Used, budget.Truncated, budget.Truncated ? Truncation : null);
    }

    private bool TryRender(
        SymbolDocument symbol,
        RepoEntry entry,
        RepoDocument repoDoc,
        MetaStore store,
        TokenBudget budget,
        bool includeBody,
        List<SymbolHit> hits)
    {
        var file = store.FindFileById(symbol.FileId);
        var header = $"{symbol.Name} ({symbol.Kind}) {repoDoc.Name}:{file?.Path ?? "?"}:{symbol.StartLine}-{symbol.EndLine}";
        var signature = symbol.Signature is null ? null : $"  {symbol.Signature}";
        var doc = symbol.DocComment is null ? null : $"  doc: {symbol.DocComment.Replace('\n', ' ')}";
        var estimate = TokenCounter.Estimate(header)
                       + (signature is null ? 0 : TokenCounter.Estimate(signature))
                       + (doc is null ? 0 : TokenCounter.Estimate(doc));
        if (!budget.TryTake(header, estimate))
        {
            return false;
        }

        string? bodyText = null;
        var bodyOmitted = false;
        if (includeBody && file is not null)
        {
            var fullPath = Path.Combine(NavigationContext.ResolveRepoRoot(_workspaceRoot, entry), file.Path.Replace('/', Path.DirectorySeparatorChar));
            bodyText = TryReadSpan(fullPath, symbol.StartLine, symbol.EndLine);
            if (bodyText is not null && !budget.TryTake(bodyText))
            {
                bodyOmitted = true;
                bodyText = null;
            }
        }

        hits.Add(new SymbolHit(
            repoDoc.Name,
            file?.Path ?? string.Empty,
            symbol.StartLine,
            symbol.EndLine,
            symbol.Name,
            symbol.Kind,
            symbol.Signature,
            symbol.ContainerName,
            symbol.DocComment,
            bodyText,
            bodyOmitted));
        return true;
    }

    private static string? TryReadSpan(string fullPath, int startLine, int endLine)
    {
        try
        {
            var lines = File.ReadAllLines(fullPath);
            var start = Math.Clamp(startLine, 1, lines.Length);
            var end = Math.Clamp(endLine, start, lines.Length);
            return string.Join("\n", lines[(start - 1)..end]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
