namespace Squeezr.Core.Indexing.Query;

public static class RrfMerger
{
    public const int K = 60;

    public static IReadOnlyList<SearchMatch> Merge(
        IReadOnlyList<(string Key, SearchMatch Match)> exactRanked,
        IReadOnlyList<(string Key, SearchMatch Match)> semanticRanked)
    {
        var scores = new Dictionary<string, double>();
        var winners = new Dictionary<string, SearchMatch>();
        var sources = new Dictionary<string, int>();

        Accumulate(exactRanked, bit: 1);
        Accumulate(semanticRanked, bit: 2);

        return winners
            .Select(pair => pair.Value with
            {
                Score = scores[pair.Key],
                Source = sources[pair.Key] == 3 ? "both" : sources[pair.Key] == 2 ? "semantic" : "exact"
            })
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Path, StringComparer.Ordinal)
            .ToList();

        void Accumulate(IReadOnlyList<(string Key, SearchMatch Match)> ranked, int bit)
        {
            for (var rank = 0; rank < ranked.Count; rank++)
            {
                var (key, match) = ranked[rank];
                if (!scores.TryAdd(key, 1.0 / (K + rank + 1)))
                {
                    scores[key] += 1.0 / (K + rank + 1);
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
