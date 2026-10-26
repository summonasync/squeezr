using Squeezr.Core.Indexing.Query;

namespace Squeezr.Cli.RankEval;

public sealed record ScoredQuery(
    GoldenQuery Source,
    double Mrr,
    double PrecisionAt5,
    double RecallAt10,
    IReadOnlyDictionary<string, int> Ranks);

public static class RankEvalScorer
{
    public const int MrrDepth = 10;
    public const int PrecisionDepth = 5;
    public const int RecallDepth = 10;

    public static ScoredQuery Score(GoldenQuery entry, IReadOnlyList<SearchMatch> matches)
    {
        var ranks = new Dictionary<string, int>();
        foreach (var expected in entry.Expected)
        {
            ranks[expected] = RankOf(expected, matches, entry.Repo);
        }

        var best = ranks.Values.Where(r => r > 0).DefaultIfEmpty(0).Min();
        var mrr = best is > 0 and <= MrrDepth ? 1.0 / best : 0.0;
        var topPaths = matches.Take(PrecisionDepth)
            .Where(m => entry.Repo is null || m.Repo == entry.Repo)
            .Select(m => m.Path)
            .ToList();
        var precision = ranks.Keys.Count(topPaths.Contains) / (double)PrecisionDepth;
        var recall = ranks.Values.Count(r => r is > 0 and <= RecallDepth) / (double)entry.Expected.Length;
        return new ScoredQuery(entry, mrr, precision, recall, ranks);
    }

    private static int RankOf(string path, IReadOnlyList<SearchMatch> matches, string? repo)
    {
        for (var i = 0; i < matches.Count; i++)
        {
            if (matches[i].Path == path && (repo is null || matches[i].Repo == repo))
            {
                return i + 1;
            }
        }

        return 0;
    }
}
