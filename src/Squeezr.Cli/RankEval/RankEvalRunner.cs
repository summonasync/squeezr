using Squeezr.Cli.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Query;
using Squeezr.Semantic.Indexing.Query;

namespace Squeezr.Cli.RankEval;

public sealed record RankEvalOutcome(
    IReadOnlyList<ScoredQuery> Rows,
    IReadOnlyList<string> Warnings,
    bool SemanticDegraded);

public sealed record SweepRow(int K, double Mrr, double P5, double Recall, bool SemanticDegraded);

public sealed record SweepOutcome(IReadOnlyList<SweepRow> Rows, IReadOnlyList<string> Warnings);

public sealed class RankEvalRunner
{
    // deep enough that a miss is a real miss, not a topK artifact; metrics cut at their own depths
    public const int ProbeDepth = 50;

    public static readonly int[] DefaultSweepGrid = [10, 30, 60, 120];

    private readonly string _workspaceRoot;

    public RankEvalRunner(string workspaceRoot) => _workspaceRoot = workspaceRoot;

    public async Task<RankEvalOutcome> RunAsync(
        IReadOnlyList<GoldenQuery> set, CancellationToken cancellationToken, FusionTuning? tuning = null)
    {
        var setupWarnings = EmbeddingRuntimeFactory.TryCreate(
            _workspaceRoot, withProvider: true, out var embeddings, out var vectorStore);
        var engine = new SearchEngine(_workspaceRoot, RankExclusion.Load(_workspaceRoot));
        var arm = embeddings is not null && vectorStore is not null
            ? new SemanticSearchArm(_workspaceRoot, embeddings, vectorStore)
            : null;

        var scored = new List<ScoredQuery>(set.Count);
        var warnings = new List<string>(setupWarnings);
        foreach (var entry in set)
        {
            var response = await engine.SearchHybridAsync(
                new SearchQuery(entry.Query, Regex: false, IgnoreCase: null, entry.Repo, PathGlob: null,
                    Language: null, TopK: ProbeDepth, MaxTokens: null, entry.Prefix),
                arm, cancellationToken, tuning: tuning);
            scored.Add(RankEvalScorer.Score(entry, response.Matches));
            warnings.AddRange(response.Warnings);
        }

        var semanticOff = embeddings is null || vectorStore is null;
        return new RankEvalOutcome(scored, warnings, semanticOff || HasVectorFailure(warnings));
    }

    public async Task<SweepOutcome> RunSweepAsync(
        IReadOnlyList<GoldenQuery> set,
        CancellationToken cancellationToken,
        IReadOnlyList<int>? kGrid = null)
    {
        var grid = kGrid ?? DefaultSweepGrid;
        var rows = new List<SweepRow>(grid.Count);
        var warnings = new List<string>();
        foreach (var k in grid)
        {
            var outcome = await RunAsync(set, cancellationToken, new FusionTuning { K = k });
            var (mrr, p5, recall) = Aggregate(outcome.Rows);
            rows.Add(new SweepRow(k, mrr, p5, recall, outcome.SemanticDegraded));
            warnings.AddRange(outcome.Warnings);
        }

        return new SweepOutcome(rows, warnings);
    }

    public static (double Mrr, double P5, double Recall) Aggregate(IReadOnlyList<ScoredQuery> rows) =>
        (rows.Average(r => r.Mrr), rows.Average(r => r.PrecisionAt5), rows.Average(r => r.RecallAt10));

    private static bool HasVectorFailure(IReadOnlyList<string> warnings) => warnings.Any(w =>
        w == SemanticSearchArm.VectorStoreUnavailableWarning ||
        w.StartsWith("vector search failed", StringComparison.Ordinal));
}
