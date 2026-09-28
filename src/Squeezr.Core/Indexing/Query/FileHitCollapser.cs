namespace Squeezr.Core.Indexing.Query;

public static class FileHitCollapser
{
    public static IReadOnlyList<SearchMatch> Collapse(IReadOnlyList<SearchMatch> ranked)
    {
        var groups = new Dictionary<(string Repo, string Path), FileGroup>();
        var ordered = new List<FileGroup>();

        foreach (var match in ranked)
        {
            var key = (match.Repo, match.Path);
            if (groups.TryGetValue(key, out var group))
            {
                group.Add(match);
                continue;
            }

            group = new FileGroup(match);
            groups[key] = group;
            ordered.Add(group);
        }

        return ordered.Select(g => g.Best).ToList();
    }

    private sealed class FileGroup(SearchMatch first)
    {
        private SearchMatch _best = first;
        private int _collapsed;

        public SearchMatch Best => _collapsed == 0 ? _best : _best with { AlsoMatched = _collapsed };

        public void Add(SearchMatch match)
        {
            _collapsed++;
            if (match.Score > _best.Score || (match.Score == _best.Score && match.Line < _best.Line))
            {
                _best = match;
            }
        }
    }
}
