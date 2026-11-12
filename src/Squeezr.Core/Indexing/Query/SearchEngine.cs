using Squeezr.Core.Embeddings;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;

namespace Squeezr.Core.Indexing.Query;

public sealed class SearchEngine
{
    public const string VectorStoreUnavailableWarning = "vector store is unavailable, falling back to exact search only";

    private static readonly TruncationHint Truncation = new(
    [
        new("raise", ["maxTokens"]),
        new("narrow", ["repo", "prefix"])
    ]);

    private readonly string _workspaceRoot;
    private readonly RankExclusion _rankExclusion;

    public SearchEngine(string workspaceRoot, RankExclusion? rankExclusion = null)
    {
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _rankExclusion = rankExclusion ?? RankExclusion.None;
    }

    public SearchResponse Search(SearchQuery query)
    {
        var guard = SecretGuardReader.Load(_workspaceRoot);
        var fileScope = ResolveFileScope(query, guard);
        var matches = ExactSearchPlan.Execute(_workspaceRoot, query, trackChunkKeys: false, fileScope, guard)
            .Select(hit => hit.Match)
            .ToList();
        return Finalize(matches, query, AppendWithheld([], guard), matches.Count, [], null);
    }

    public async Task<SearchResponse> SearchHybridAsync(
        SearchQuery query,
        IEmbeddingProvider? embeddings,
        IVectorStore? vectorStore,
        CancellationToken ct,
        SemanticCoverage? coverage = null,
        FusionTuning? tuning = null)
    {
        var guard = SecretGuardReader.Load(_workspaceRoot);
        var fileScope = ResolveFileScope(query, guard);
        var indexPath = SqueezrPaths.TrigramIndexPath(_workspaceRoot);
        List<(SearchMatch Match, string? Key)> exact;
        List<(SearchMatch Match, string? Key)> identifierHits;
        if (query.Regex || !File.Exists(indexPath))
        {
            // regex rows never reach the leg and a missing index stays ExactSearchPlan's throw —
            // both keep the standalone pipeline; the branch below owns every row the leg can run on
            exact = ExactSearchPlan.Execute(_workspaceRoot, query, trackChunkKeys: true, fileScope, guard);
            identifierHits = [];
        }
        else
        {
            // one store + one parsed trigram index per hybrid query, shared by both exact arms —
            // the leg used to open the store and re-parse the whole index file a second time
            using var store = new MetaStore(SqueezrPaths.MetaDbPath(_workspaceRoot));
            var reader = new TrigramIndexReader(indexPath);
            exact = ExactSearchPlan.Execute(_workspaceRoot, store, reader, query, trackChunkKeys: true, fileScope, guard);
            // a guard interception is why the phrase arm died, not a phrase that never existed:
            // the token leg would answer a secret query with files sharing its common fragments
            identifierHits = guard.WithheldCount > 0
                ? []
                : IdentifierTokenLeg.Execute(_workspaceRoot, store, reader, query, fileScope, guard);
        }
        var semantic = SemanticArm.Empty;
        var warnings = new List<string>();

        if (vectorStore is not null && embeddings is not null)
        {
            try
            {
                if (!await vectorStore.IsAvailableAsync(ct))
                {
                    warnings.Add(VectorStoreUnavailableWarning);
                }
                else if (await vectorStore.CountAsync(ct) > 0)
                {
                    semantic = await SemanticHitsResolver.Resolve(
                        _workspaceRoot, query, embeddings, vectorStore, ct, fileScope, guard);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"vector search failed ({ex.Message}), falling back to exact search only");
            }

            if (coverage is { TotalChunks: > 0, PendingChunks: > 0 })
            {
                warnings.Add(FormatCoverageWarning(coverage));
            }
        }

        var phraseLocations = new HashSet<(string Path, int Line)>(
            exact.Select(hit => (hit.Match.Path, hit.Match.Line)));
        var dedupedIdentifier = identifierHits
            .Where(hit => !phraseLocations.Contains((hit.Match.Path, hit.Match.Line)))
            .ToList();

        var exactRanked = exact
            .Concat(dedupedIdentifier)
            .Select(hit => (hit.Key!, hit.Match))
            .ToList();

        // sits after the exact ranking settles: exactRanked.Count is the same number Finalize
        // hands to WeakMatchPolicy, and both policies must answer for one exact arm
        if (fileScope is not { Count: 0 })
        {
            var capped = CappedArmPolicy.Warning(
                semantic, exactRanked.Count, query.Repo, query.Prefix, query.Kind, query.PathGlob);
            if (capped is not null)
            {
                warnings.Add(capped);
            }
        }

        var merged = RrfMerger.Merge(exactRanked, semantic.Hits, tuning);
        var semanticScores = semantic.Hits.Select(hit => hit.Match.Score).ToList();
        return Finalize(merged, query, AppendWithheld(warnings, guard), exactRanked.Count, semanticScores, embeddings?.ModelKey);
    }

    private static List<string> AppendWithheld(List<string> warnings, SecretGuardReader guard)
    {
        if (guard.Warnings is { } withheld)
        {
            warnings.AddRange(withheld);
        }

        return warnings;
    }

    private HashSet<uint>? ResolveFileScope(SearchQuery query, SecretGuardReader guardReader)
    {
        var prefix = PathPrefix.Normalize(query.Prefix);
        if (prefix is not null && guardReader.IsProtected(prefix))
        {
            throw guardReader.Refusal(prefix);
        }

        var excluding = _rankExclusion.IsActive;
        if (prefix is null && !excluding && query.PathGlob is null && query.Language is null && query.Repo is null)
        {
            return null;
        }

        // an unbuilt index is ExactSearchPlan's error to report, with its canonical index_not_built code
        if (!File.Exists(SqueezrPaths.TrigramIndexPath(_workspaceRoot)))
        {
            return null;
        }

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(_workspaceRoot));
        uint? repoId = null;
        if (query.Repo is not null)
        {
            var repo = store.FindRepoIgnoringCase(query.Repo);
            if (repo is null)
            {
                return [];
            }

            repoId = repo.Id;
        }

        bool InScope(FileDocument file)
        {
            if (query.PathGlob is not null && !GlobMatcher.IsMatch(query.PathGlob, file.Path))
            {
                return false;
            }

            if (query.Language is not null && !string.Equals(file.Language, query.Language, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (prefix is not null && !PathPrefix.Matches(prefix, file.Path))
            {
                return false;
            }

            return !_rankExclusion.Matches(file.Path);
        }

        var scope = FileScope.Resolve(store, InScope, repoId);
        if (prefix is null || scope.Count > 0)
        {
            return scope;
        }

        // a glob or language emptying an existing prefix subtree is a valid zero-hit answer, and
        // rank-excluded files stay indexed, so an excluded-away subtree answers empty too — only
        // a prefix matching nothing at all is the "no such subtree" error
        if (FileScope.Resolve(store, file => PathPrefix.Matches(prefix, file.Path), repoId).Count == 0)
        {
            var indexedPaths = (repoId is { } id ? store.ListFiles(id) : store.ListAllFiles())
                .Select(f => f.Path)
                .Where(path => !guardReader.IsProtected(path));
            throw PathPrefix.NoSubtreeException(prefix, indexedPaths);
        }

        return scope;
    }

    private static string FormatCoverageWarning(SemanticCoverage coverage)
    {
        var percent = (int)Math.Round(coverage.Ratio!.Value * 100, MidpointRounding.AwayFromZero);
        var noun = coverage.PendingChunks == 1 ? "chunk" : "chunks";
        return $"semantic coverage: {percent}% ({coverage.PendingChunks} {noun} pending embedding)";
    }

    private static SearchResponse Finalize(
        IReadOnlyList<SearchMatch> ranked,
        SearchQuery query,
        IReadOnlyList<string> warnings,
        int exactArmHits,
        IReadOnlyList<double> semanticScores,
        string? modelKey)
    {
        // collapse before the cap so topK counts files, not spans of one file
        var files = FileHitCollapser.Collapse(ranked);
        var limited = query.TopK is > 0 ? files.Take(query.TopK.Value).ToList() : files.ToList();

        var budget = query.MaxTokens;
        var perHit = SnippetBudget.PerHitTokens(budget, query.TopK);
        var tokensUsed = 0;
        var exactTokens = 0;
        var semanticTokens = 0;
        var taken = new List<SearchMatch>();
        var truncated = false;
        foreach (var match in limited)
        {
            var snippet = match.Snippet;
            var estimate = TokenCounter.Estimate(snippet);
            var clipped = false;
            if (perHit is { } cap && estimate > cap)
            {
                snippet = SnippetBudget.Clip(snippet, cap);
                estimate = TokenCounter.Estimate(snippet);
                clipped = true;
            }

            if (budget is > 0 && taken.Count > 0 && tokensUsed + estimate > budget)
            {
                truncated = true;
                break;
            }

            tokensUsed += estimate;
            if (match.Source == "semantic")
            {
                semanticTokens += estimate;
            }
            else
            {
                exactTokens += estimate;
            }

            taken.Add(clipped ? match with { Snippet = snippet } : match);
        }

        var weakNotice = WeakMatchPolicy.IsWeak(exactArmHits, semanticScores, modelKey)
            ? WeakMatchPolicy.Warning(taken.Count(match => match.Source == "semantic"), query.Kind)
            : UncalibratedFloorNotice(semanticScores, modelKey);

        return new SearchResponse(taken, tokensUsed, truncated, truncated ? Truncation : null)
        {
            Warnings = weakNotice is null ? warnings : [.. warnings, weakNotice],
            LegUsage = taken.Count == 0 ? null : new SearchLegUsage(exactTokens, semanticTokens)
        };
    }

    // the uncalibrated twin of the low-confidence sentence: the scores are there but no measured floor
    // speaks for the model that produced them, so the answer claims nothing and says which key is bare
    private static string? UncalibratedFloorNotice(IReadOnlyList<double> semanticScores, string? modelKey) =>
        semanticScores.Count > 0 && modelKey is not null && !WeakMatchPolicy.HasCalibratedFloor(modelKey)
            ? WeakMatchPolicy.CalibrationHint(modelKey)
            : null;
}
