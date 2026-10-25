using Squeezr.Cli.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Query;

namespace Squeezr.Cli.RankEval;

public sealed record RankEvalOutcome(
    IReadOnlyList<ScoredQuery> Rows,
    IReadOnlyList<string> Warnings,
    bool SemanticDegraded);

public sealed class RankEvalRunner
{
    // deep enough that a miss is a real miss, not a topK artifact; metrics cut at their own depths
    public const int ProbeDepth = 50;

    private readonly string _workspaceRoot;

    public RankEvalRunner(string workspaceRoot) => _workspaceRoot = workspaceRoot;

    public async Task<RankEvalOutcome> RunAsync(
        IReadOnlyList<GoldenQuery> set, CancellationToken cancellationToken)
    {
        var setupWarnings = EmbeddingRuntimeFactory.TryCreate(
            _workspaceRoot, withProvider: true, out var embeddings, out var vectorStore);
        var engine = new SearchEngine(_workspaceRoot, RankExclusion.Load(_workspaceRoot));

        var scored = new List<ScoredQuery>(set.Count);
        var warnings = new List<string>(setupWarnings);
        foreach (var entry in set)
        {
            var response = await engine.SearchHybridAsync(
                new SearchQuery(entry.Query, Regex: false, IgnoreCase: null, entry.Repo, PathGlob: null,
                    Language: null, TopK: ProbeDepth, MaxTokens: null, entry.Prefix),
                embeddings, vectorStore, cancellationToken);
            scored.Add(RankEvalScorer.Score(entry, response.Matches));
            warnings.AddRange(response.Warnings);
        }

        var semanticOff = embeddings is null || vectorStore is null;
        return new RankEvalOutcome(scored, warnings, semanticOff || HasVectorFailure(warnings));
    }

    private static bool HasVectorFailure(IReadOnlyList<string> warnings) => warnings.Any(w =>
        w == SearchEngine.VectorStoreUnavailableWarning ||
        w.StartsWith("vector search failed", StringComparison.Ordinal));
}
