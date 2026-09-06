using System.Text.RegularExpressions;
using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Query;

public sealed class SearchEngine
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private readonly string _workspaceRoot;

    public SearchEngine(string workspaceRoot) => _workspaceRoot = Path.GetFullPath(workspaceRoot);

    public SearchResponse Search(SearchQuery query)
    {
        var indexPath = SqueezrPaths.TrigramIndexPath(_workspaceRoot);
        if (!File.Exists(indexPath))
        {
            throw new WorkspaceException("Index is not built. Run 'squeezr index' first.");
        }

        if (query.Regex)
        {
            try
            {
                _ = new Regex(query.Query, RegexOptions.None, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new WorkspaceException($"Invalid regex pattern: {ex.Message}");
            }
        }

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(_workspaceRoot));
        using var reader = new TrigramIndexReader(indexPath);

        var manifest = WorkspaceManifestFile.Load(_workspaceRoot);
        var reposById = store.ListRepos().ToDictionary(r => r.Id);
        var entriesByName = manifest.Repos.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var filter = BuildFilter(store, query);

        var caseSensitive = ResolveCaseSensitivity(query);
        var candidates = PlanCandidates(reader, query, filter, caseSensitive);

        var matches = new List<SearchMatch>();
        foreach (var docId in candidates)
        {
            var (repoId, fileId) = reader.GetDoc(docId);
            if (!reposById.TryGetValue(repoId, out var repoDoc)
                || !entriesByName.TryGetValue(repoDoc.Name, out var entry))
            {
                continue;
            }

            var file = store.FindFileById(fileId);
            if (file is null)
            {
                continue;
            }

            var fullPath = Path.Combine(ResolveRepoRoot(entry), file.Path.Replace('/', Path.DirectorySeparatorChar));
            string content;
            try
            {
                content = File.ReadAllText(fullPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!TryVerify(content, query, caseSensitive, out var line, out var snippet))
            {
                continue;
            }

            matches.Add(new SearchMatch(
                repoDoc.Name,
                file.Path,
                line,
                Score(file, query, caseSensitive),
                snippet));
        }

        var ranked = matches
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Path, StringComparer.Ordinal)
            .ToList();

        if (query.TopK is > 0)
        {
            ranked = ranked.Take(query.TopK.Value).ToList();
        }

        var budget = query.MaxTokens;
        var tokensUsed = 0;
        var taken = new List<SearchMatch>();
        var truncated = false;
        foreach (var match in ranked)
        {
            if (budget is > 0 && taken.Count > 0 && tokensUsed + TokenCounter.Estimate(match.Snippet) > budget)
            {
                truncated = true;
                break;
            }

            tokensUsed += TokenCounter.Estimate(match.Snippet);
            taken.Add(match);
        }

        return new SearchResponse(taken, tokensUsed, truncated);
    }

    private static bool ResolveCaseSensitivity(SearchQuery query) =>
        query.IgnoreCase ?? (query.Query != query.Query.ToLowerInvariant());

    private static List<uint> PlanCandidates(TrigramIndexReader reader, SearchQuery query, HashSet<uint>? filter, bool caseSensitive)
    {
        List<string[]> trigramGroups;
        if (query.Regex)
        {
            trigramGroups = RegexQueryAnalyzer.ExtractTrigramGroups(query.Query);
            if (trigramGroups.Count == 0)
            {
                return FullScan(reader, filter);
            }
        }
        else if (query.Query.Length >= 3)
        {
            trigramGroups = PlanLiteral(query.Query, caseSensitive, out var exceededCap);
            if (exceededCap)
            {
                return FullScan(reader, filter);
            }
        }
        else
        {
            return FullScan(reader, filter);
        }

        var lists = trigramGroups
            .Select(group =>
            {
                var union = new HashSet<uint>();
                foreach (var trigram in group)
                {
                    var postings = reader.Find(TrigramHasher.ComputeHash(trigram[0], trigram[1], trigram[2]));
                    if (postings is not null)
                    {
                        union.UnionWith(postings.DocIds);
                    }
                }

                return filter is null
                    ? union.ToArray()
                    : union.Where(docId => filter.Contains(reader.GetDoc(docId).FileId)).ToArray();
            })
            .ToList();

        return PostingListIntersect.Intersect(lists).ToList();
    }

    private static List<string[]> PlanLiteral(string literal, bool caseSensitive, out bool exceededCap)
    {
        exceededCap = false;
        var groups = new List<string[]>(literal.Length - 2);
        for (var i = 0; i + 3 <= literal.Length; i++)
        {
            var trigram = literal.Substring(i, 3);
            groups.Add(caseSensitive ? [trigram] : ExpandWithCap(trigram, literal.Length - 2, ref exceededCap));
        }

        return groups;
    }

    private static string[] ExpandWithCap(string trigram, int trigramCount, ref bool exceededCap)
    {
        if (exceededCap)
        {
            return [trigram];
        }

        var variants = CaseVariants.Expand(trigram).Distinct(StringComparer.Ordinal).ToArray();
        var budget = Math.Max(1, RankingWeights.CaseVariantCap / trigramCount);
        if (variants.Length > budget)
        {
            exceededCap = true;
            return [trigram];
        }

        return variants;
    }

    private static List<uint> FullScan(TrigramIndexReader reader, HashSet<uint>? filter)
    {
        var result = new List<uint>();
        for (uint docId = 0; docId < reader.DocCount; docId++)
        {
            if (filter is null || filter.Contains(reader.GetDoc(docId).FileId))
            {
                result.Add(docId);
            }
        }

        return result;
    }

    private HashSet<uint>? BuildFilter(MetaStore store, SearchQuery query)
    {
        if (query.Repo is null && query.PathGlob is null && query.Language is null)
        {
            return null;
        }

        var filter = new HashSet<uint>();
        foreach (var repo in store.ListRepos())
        {
            if (query.Repo is not null && !string.Equals(repo.Name, query.Repo, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in store.ListFiles(repo.Id))
            {
                if (query.PathGlob is not null && !GlobMatcher.IsMatch(query.PathGlob, file.Path))
                {
                    continue;
                }

                if (query.Language is not null && !string.Equals(file.Language, query.Language, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                filter.Add(file.Id);
            }
        }

        return filter;
    }

    private static bool TryVerify(string content, SearchQuery query, bool caseSensitive, out int line, out string snippet)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int index;
        if (query.Regex)
        {
            var options = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            Match match;
            try
            {
                match = Regex.Match(content, query.Query, options, RegexTimeout);
            }
            catch (RegexMatchTimeoutException)
            {
                line = 0;
                snippet = string.Empty;
                return false;
            }

            if (!match.Success)
            {
                line = 0;
                snippet = string.Empty;
                return false;
            }

            index = match.Index;
        }
        else
        {
            index = content.IndexOf(query.Query, comparison);
            if (index < 0)
            {
                line = 0;
                snippet = string.Empty;
                return false;
            }
        }

        line = content.AsSpan(0, index).Count('\n') + 1;
        snippet = ExtractLine(content, index);
        return true;
    }

    private static string ExtractLine(string content, int index)
    {
        var start = content.LastIndexOf('\n', Math.Min(index, content.Length - 1)) + 1;
        var end = content.IndexOf('\n', index);
        if (end < 0)
        {
            end = content.Length;
        }

        var line = content[start..end].Trim();
        return line.Length <= 200 ? line : line[..200];
    }

    private static double Score(FileDocument file, SearchQuery query, bool caseSensitive)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var fileName = Path.GetFileName(file.Path);
        var score = fileName.IndexOf(query.Query, comparison) >= 0
            ? RankingWeights.FilenameMatchBonus + RankingWeights.BodyMatchBase
            : RankingWeights.BodyMatchBase;

        if (fileName.Contains("Tests", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
        {
            score *= RankingWeights.TestFileMultiplier;
        }

        score += Math.Max(0.0, RankingWeights.SizeBonusCap * (1.0 - (double)file.SizeBytes / RankingWeights.SizeBonusReferenceBytes));
        return score;
    }

    private string ResolveRepoRoot(RepoEntry repo) =>
        Path.GetFullPath(Path.IsPathRooted(repo.Path) ? repo.Path : Path.Combine(_workspaceRoot, repo.Path));
}
