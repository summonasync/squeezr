namespace Squeezr.Core.Indexing.Query;

public static class RrfMerger
{
    public const int K = 60;

    public static IReadOnlyList<SearchMatch> Merge(
        IReadOnlyList<(string Key, SearchMatch Match)> exactRanked,
        IReadOnlyList<(string Key, SearchMatch Match)> semanticRanked,
        FusionTuning? tuning = null)
    {
        var k = tuning?.K ?? K;
        var exactWeight = tuning?.ExactWeight ?? 1.0;
        var semanticWeight = tuning?.SemanticWeight ?? 1.0;
        var scores = new Dictionary<string, double>();
        var winners = new Dictionary<string, SearchMatch>();
        var sources = new Dictionary<string, int>();

        Accumulate(exactRanked, bit: 1, exactWeight);
        Accumulate(semanticRanked, bit: 2, semanticWeight);

        return winners
            .Select(pair => pair.Value with
            {
                Score = scores[pair.Key],
                Source = sources[pair.Key] == 3 ? "both" : sources[pair.Key] == 2 ? "semantic" : "exact"
            })
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Path, StringComparer.Ordinal)
            .ToList();

        void Accumulate(IReadOnlyList<(string Key, SearchMatch Match)> ranked, int bit, double weight)
        {
            for (var rank = 0; rank < ranked.Count; rank++)
            {
                var (key, match) = ranked[rank];
                if (!scores.TryAdd(key, weight / (k + rank + 1)))
                {
                    scores[key] += weight / (k + rank + 1);
                }

                if (!winners.TryAdd(key, match))
                {
                    winners[key] = MergeWinner(winners[key], match, sources[key], bit);
                }

                sources[key] = sources.TryGetValue(key, out var prev) ? prev | bit : bit;
            }
        }

        static SearchMatch MergeWinner(SearchMatch existing, SearchMatch incoming, int prevSources, int bit)
        {
            if (prevSources == 1 && bit == 2)
            {
                return existing;
            }

            return incoming;
        }
    }
}
