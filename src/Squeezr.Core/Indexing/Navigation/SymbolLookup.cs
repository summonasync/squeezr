using System.Text.Json.Serialization;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Navigation;

public sealed record SymbolMember(string Name, string Kind, int Line);

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
    bool BodyOmitted,
    IReadOnlyList<SymbolMember> Members,
    int MembersOmitted);

public sealed record SymbolLookupResult(
    IReadOnlyList<SymbolHit> Matches,
    int TokensUsed,
    bool Truncated,
    TruncationHint? Hint,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? DidYouMean,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? GuardWarnings,
    [property: JsonIgnore]
    AnswerState State,
    [property: JsonIgnore]
    int RefusedBodyTokens);

public sealed class SymbolLookup
{
    public const int DefaultMaxTokens = 300;

    private static readonly TruncationHint Truncation = new(
    [
        new("narrow the query", ["repo", "kind"]),
        new("raise", ["maxTokens"])
    ]);

    private static readonly TruncationHint MembersTruncation = new(
    [
        new("read a member with", ["expand_span"]),
        new("raise", ["maxTokens"])
    ]);

    private const double DidYouMeanThreshold = 0.34;
    private const int DidYouMeanTopN = 3;

    private readonly string _workspaceRoot;

    public SymbolLookup(string workspaceRoot) => _workspaceRoot = NavigationContext.RequireIndex(workspaceRoot);

    public SymbolLookupResult Find(string name, string? repo, string? kind, int maxTokens, bool body)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(_workspaceRoot));
        return Find(name, repo, kind, maxTokens, body, store);
    }

    public SymbolLookupResult Find(string name, string? repo, string? kind, int maxTokens, bool body, MetaStore store)
    {
        var manifest = WorkspaceManifestFile.Load(_workspaceRoot);
        var guard = SecretGuardReader.Load(_workspaceRoot);
        var budget = new TokenBudget(maxTokens);
        var hits = new List<SymbolHit>();

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
            if (!TryRender(symbol, entry, repoDoc, store, budget, includeBody, hits, guard))
            {
                return Answer(hits, budget, AnswerState.Clipped, HintFor(hits), null, guard);
            }
        }

        if (hits.Count == 0)
        {
            var candidates = FindDidYouMean(name, repo, kind, store, manifest, guard);
            IReadOnlyList<string>? didYouMean = candidates.Count > 0 ? candidates : null;
            if (didYouMean is not null)
            {
                var suggestionText = $"did you mean: {string.Join(", ", didYouMean)}";
                if (!budget.TryTake(suggestionText))
                {
                    return Answer([], budget, AnswerState.Clipped, null, null, guard);
                }
            }

            return Answer([], budget, AnswerState.Complete, null, didYouMean, guard);
        }

        return Answer(
            hits, budget, StateOf(hits), budget.Truncated ? HintFor(hits) : null, null, guard);
    }

    private static SymbolLookupResult Answer(
        List<SymbolHit> hits,
        TokenBudget budget,
        AnswerState state,
        TruncationHint? hint,
        IReadOnlyList<string>? didYouMean,
        SecretGuardReader guard) =>
        new(hits, budget.Used, budget.Truncated, hint, didYouMean, guard.Warnings, state, budget.RefusedBodyTokens);

    private List<string> FindDidYouMean(
        string name,
        string? repo,
        string? kind,
        MetaStore store,
        WorkspaceManifest manifest,
        SecretGuardReader guard)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var scored = new List<(string Name, double Score, uint FileId)>();

        foreach (var entry in NavigationContext.SelectRepos(manifest, repo))
        {
            var repoDoc = store.FindRepo(entry.Name);
            if (repoDoc is null)
            {
                continue;
            }

            foreach (var symbol in store.ListSymbolsByRepo(repoDoc.Id))
            {
                if (kind is not null && !symbol.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!seen.Add(symbol.Name))
                {
                    continue;
                }

                var score = TrigramJaccard.Similarity(name, symbol.Name);
                if (score >= DidYouMeanThreshold)
                {
                    scored.Add((symbol.Name, score, symbol.FileId));
                }
            }
        }

        // a suggestion is a name read out of a file the guard hides, so the sweep stops at the same wall;
        // the file lookup rides only on candidates that cleared the score threshold
        var taken = new List<string>(DidYouMeanTopN);
        foreach (var candidate in scored
                     .OrderByDescending(s => s.Score)
                     .ThenBy(s => s.Name.Length)
                     .ThenBy(s => s.Name, StringComparer.Ordinal))
        {
            if (taken.Count == DidYouMeanTopN)
            {
                break;
            }

            var file = store.FindFileById(candidate.FileId);
            if (file is not null && guard.IsProtected(file.Path))
            {
                continue;
            }

            taken.Add(candidate.Name);
        }

        return taken;
    }

    private static TruncationHint HintFor(List<SymbolHit> hits) =>
        hits.Any(hit => hit.BodyOmitted && hit.Members.Count > 0) ? MembersTruncation : Truncation;

    private static AnswerState StateOf(List<SymbolHit> hits)
    {
        var state = AnswerState.Complete;
        foreach (var hit in hits)
        {
            var refused = HitState(hit);
            if (refused > state)
            {
                state = refused;
            }
        }

        return state;
    }

    // an empty map next to a refused body means the rows never fit, not that the symbol had no members
    private static AnswerState HitState(SymbolHit hit)
    {
        if (!hit.BodyOmitted)
        {
            return AnswerState.Complete;
        }

        return hit.Members.Count > 0 && hit.MembersOmitted == 0 ? AnswerState.Mapped : AnswerState.Clipped;
    }

    private bool TryRender(
        SymbolDocument symbol,
        RepoEntry entry,
        RepoDocument repoDoc,
        MetaStore store,
        TokenBudget budget,
        bool includeBody,
        List<SymbolHit> hits,
        SecretGuardReader guard)
    {
        var file = store.FindFileById(symbol.FileId);
        if (file is not null && guard.TryWithhold(file.Path))
        {
            return true;
        }

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
        IReadOnlyList<SymbolMember> members = [];
        var membersOmitted = 0;
        if (includeBody && file is not null)
        {
            var fullPath = Path.Combine(NavigationContext.ResolveRepoRoot(_workspaceRoot, entry), file.Path.Replace('/', Path.DirectorySeparatorChar));
            bodyText = TryReadSpan(fullPath, symbol.StartLine, symbol.EndLine);
            if (bodyText is not null && !budget.TryTake(bodyText, isBody: true))
            {
                bodyOmitted = true;
                bodyText = null;
                if (TypeKinds.Contains(symbol.Kind))
                {
                    (members, membersOmitted) = TakeMembers(store, symbol, budget);
                }
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
            bodyOmitted,
            members,
            membersOmitted));
        return true;
    }

    private static (IReadOnlyList<SymbolMember> Members, int Omitted) TakeMembers(
        MetaStore store,
        SymbolDocument container,
        TokenBudget budget)
    {
        var candidates = store.ListSymbolsByFile(container.FileId)
            .Where(candidate => candidate.Id != container.Id && IsMemberOf(candidate, container))
            .OrderBy(candidate => candidate.StartLine)
            .ToList();
        var taken = new List<SymbolMember>(candidates.Count);
        var omitted = 0;
        foreach (var candidate in candidates)
        {
            if (!budget.TryTake($"{candidate.Name} ({candidate.Kind}) {candidate.StartLine}"))
            {
                omitted = candidates.Count - taken.Count;
                break;
            }

            taken.Add(new SymbolMember(candidate.Name, candidate.Kind, candidate.StartLine));
        }

        return (taken, omitted);
    }

    private static bool IsMemberOf(SymbolDocument candidate, SymbolDocument container)
    {
        if (candidate.ContainerName is not { } owner)
        {
            return false;
        }

        // a generic type is indexed by its bare name while its members carry 'Name<T>' as the container
        return owner == container.Name || owner.StartsWith($"{container.Name}<", StringComparison.Ordinal);
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
