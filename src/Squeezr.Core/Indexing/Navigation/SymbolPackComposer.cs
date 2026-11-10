using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Navigation;

public sealed record PackCaller(string Repo, string File, int Line, string Kind, string? EnclosingSymbol);

public sealed record SymbolPack(
    string Name,
    string Kind,
    string Repo,
    string File,
    string Span,
    string? Signature,
    string? ContainerName,
    string? DocComment,
    IReadOnlyList<string> BodyLines,
    bool BodyOmitted,
    IReadOnlyList<SymbolMember> Members,
    int MembersOmitted,
    IReadOnlyList<PackCaller> Callers,
    int WithheldCallers,
    int LookupTokens,
    int CallerTokens,
    int BudgetTokens,
    bool LookupTruncated,
    int AdditionalMatches,
    IReadOnlyList<GhostRefDeclaration> LaggingDeclarations,
    IReadOnlyList<string> GuardWarnings,
    IReadOnlyList<string>? DidYouMean,
    AnswerState State)
{
    public bool Complete => WithheldCallers == 0 && State is not AnswerState.Clipped;
}

public sealed class SymbolPackComposer
{
    private readonly string _workspaceRoot;

    public SymbolPackComposer(string workspaceRoot) =>
        _workspaceRoot = NavigationContext.RequireIndex(workspaceRoot);

    public SymbolPack Compose(string name, string? repo, string? kind, int maxTokens, MetaStore? store = null)
    {
        using var owned = store is null ? new MetaStore(SqueezrPaths.MetaDbPath(_workspaceRoot)) : null;
        var session = store ?? owned!;

        var lookup = new SymbolLookup(_workspaceRoot).Find(name, repo, kind, maxTokens, body: true, session);
        if (lookup.Matches.Count == 0)
        {
            return new SymbolPack(
                name, kind ?? "symbol", repo ?? string.Empty, string.Empty, "0-0", null, null, null,
                [], false, [], 0, [], 0, lookup.TokensUsed, 0, maxTokens,
                lookup.State is AnswerState.Clipped, 0, [],
                lookup.GuardWarnings ?? [], lookup.DidYouMean, lookup.State);
        }

        var hit = lookup.Matches[0];
        var remaining = maxTokens - lookup.TokensUsed;
        var refs = new RefLookup(_workspaceRoot).Find(hit.Name, hit.Repo, session);
        var rows = refs.Groups
            .SelectMany(g => g.Refs.Select(r => (g.Repo, Hit: r)))
            .DistinctBy(x => (x.Repo, x.Hit.File, x.Hit.Line, x.Hit.Kind, x.Hit.EnclosingSymbol))
            .Select(x => new PackCaller(x.Repo, x.Hit.File, x.Hit.Line, x.Hit.Kind, x.Hit.EnclosingSymbol))
            .ToList();
        BackfillEnclosingSymbols(session, rows);

        var taken = new List<PackCaller>();
        var callerTokens = 0;
        foreach (var row in rows)
        {
            var label = row.EnclosingSymbol is null
                ? $"{row.Repo}:{row.File}:{row.Line} {row.Kind}"
                : $"{row.Repo}:{row.File}:{row.Line} {row.Kind} in {row.EnclosingSymbol}";
            var cost = TokenCounter.Estimate(label);
            if (callerTokens + cost > remaining)
            {
                break;
            }

            callerTokens += cost;
            taken.Add(row);
        }

        var guardWarnings = (lookup.GuardWarnings ?? []).Concat(refs.GuardWarnings ?? []).Distinct().ToList();

        return new SymbolPack(
            hit.Name,
            hit.Kind,
            hit.Repo,
            hit.File,
            $"{hit.StartLine}-{hit.EndLine}",
            hit.Signature,
            hit.ContainerName,
            hit.DocComment,
            hit.Body?.Split('\n') ?? [],
            hit.BodyOmitted,
            hit.Members,
            hit.MembersOmitted,
            taken,
            rows.Count - taken.Count,
            lookup.TokensUsed,
            callerTokens,
            maxTokens,
            lookup.State is AnswerState.Clipped,
            lookup.Matches.Count - 1,
            refs.LaggingDeclarations ?? [],
            guardWarnings,
            lookup.DidYouMean,
            lookup.State);
    }

    private static void BackfillEnclosingSymbols(MetaStore store, List<PackCaller> rows)
    {
        if (rows.All(r => r.EnclosingSymbol is not null))
        {
            return;
        }

        var symbolsByFile = new Dictionary<(string Repo, string File), IReadOnlyList<SymbolDocument>>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.EnclosingSymbol is not null)
            {
                continue;
            }

            if (!symbolsByFile.TryGetValue((row.Repo, row.File), out var symbols))
            {
                var repoDoc = store.FindRepo(row.Repo);
                var fileDoc = repoDoc is null
                    ? null
                    : store.ListFiles(repoDoc.Id).FirstOrDefault(f => f.Path.Equals(row.File, StringComparison.Ordinal));
                symbols = fileDoc is null ? [] : store.ListSymbolsByFile(fileDoc.Id);
                symbolsByFile[(row.Repo, row.File)] = symbols;
            }

            var container = symbols
                .Where(s => s.StartLine <= row.Line && row.Line <= s.EndLine)
                .OrderBy(s => s.EndLine - s.StartLine)
                .Select(s => s.ContainerName is null ? s.Name : $"{s.ContainerName}.{s.Name}")
                .FirstOrDefault();
            if (container is not null)
            {
                rows[i] = row with { EnclosingSymbol = container };
            }
        }
    }
}
