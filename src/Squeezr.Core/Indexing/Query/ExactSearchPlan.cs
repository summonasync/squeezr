using System.Text.RegularExpressions;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Query;

public static class ExactSearchPlan
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    public static List<(SearchMatch Match, string? Key)> Execute(
        string workspaceRoot,
        SearchQuery query,
        bool trackChunkKeys,
        IReadOnlySet<uint>? fileScope,
        SecretGuardReader guard)
    {
        var indexPath = SqueezrPaths.TrigramIndexPath(workspaceRoot);
        if (!File.Exists(indexPath))
        {
            throw new WorkspaceException(
                NavigationContext.MissingIndexMessage,
                WorkspaceException.IndexNotBuiltCode);
        }

        if (query.Regex)
        {
            try
            {
                _ = new Regex(query.Query, RegexOptions.None, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new WorkspaceException(
                    $"Invalid regex pattern: {ex.Message}",
                    WorkspaceException.InvalidQueryCode);
            }
        }

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot));
        var reader = new TrigramIndexReader(indexPath);
        return Execute(workspaceRoot, store, reader, query, trackChunkKeys, fileScope, guard);
    }

    // the hybrid path hands the phrase arm and the identifier leg one per-query store+reader,
    // so the index file is parsed once per query instead of once per arm
    internal static List<(SearchMatch Match, string? Key)> Execute(
        string workspaceRoot,
        MetaStore store,
        TrigramIndexReader reader,
        SearchQuery query,
        bool trackChunkKeys,
        IReadOnlySet<uint>? fileScope,
        SecretGuardReader guard)
    {
        var manifest = WorkspaceManifestFile.Load(workspaceRoot);
        var reposById = store.ListRepos().ToDictionary(r => r.Id);
        var entriesByName = manifest.Repos.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);
        var filter = BuildFilter(store, query, fileScope);

        var caseSensitive = ResolveCaseSensitivity(query);
        var candidates = PlanCandidates(reader, query, filter, caseSensitive);

        var matches = new List<(SearchMatch Match, string? Key)>();
        foreach (var docId in candidates)
        {
            var (repoId, fileId) = reader.GetDoc(docId);
            if (!reposById.TryGetValue(repoId, out var repoDoc)
                || !entriesByName.TryGetValue(repoDoc.Name, out var entry))
            {
                continue;
            }

            var file = store.FindFileById(fileId);
            if (file is null || guard.TryWithhold(file.Path))
            {
                continue;
            }

            var symbols = store.ListSymbolsByFile(file.Id);

            var fullPath = Path.Combine(ResolveRepoRoot(workspaceRoot, entry), file.Path.Replace('/', Path.DirectorySeparatorChar));
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

            string? key = null;
            if (trackChunkKeys)
            {
                key = ResolveChunkKey(store, file.Id, line);
            }

            matches.Add((new SearchMatch(
                repoDoc.Name,
                file.Path,
                line,
                Score(file, symbols, query, caseSensitive, snippet),
                snippet), key));
        }

        return matches
            .OrderByDescending(hit => hit.Match.Score)
            .ThenBy(hit => hit.Match.Path, StringComparer.Ordinal)
            .ToList();
    }

    private static bool ResolveCaseSensitivity(SearchQuery query) =>
        query.IgnoreCase ?? (query.Query != query.Query.ToLowerInvariant());

    private static IReadOnlyList<uint> PlanCandidates(TrigramIndexReader reader, SearchQuery query, IReadOnlySet<uint>? filter, bool caseSensitive)
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
            trigramGroups = LiteralGroups(query.Query, caseSensitive, out var exceededCap);
            if (exceededCap)
            {
                return FullScan(reader, filter);
            }
        }
        else
        {
            return FullScan(reader, filter);
        }

        return ResolveCandidates(reader, trigramGroups, filter);
    }

    internal static List<string[]> LiteralGroups(string literal, bool caseSensitive, out bool exceededCap)
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

    internal static uint[] ResolveCandidates(
        TrigramIndexReader reader,
        IReadOnlyList<string[]> groups,
        IReadOnlySet<uint>? filter)
    {
        var lists = groups
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

                return IntersectInput(union, filter, reader);
            })
            .ToList();

        return PostingListIntersect.Intersect(lists);
    }

    // the same set in the same order as ResolveCandidates, but every hash is answered by a
    // per-doc scan of the postings it needs anyway — on a cold reader the Find path first pays
    // a full inverted-index build, which a handful of identifier tokens never justifies
    internal static uint[] ResolveCandidatesByScan(
        TrigramIndexReader reader,
        IReadOnlyList<string[]> groups,
        IReadOnlySet<uint>? filter)
    {
        var lists = groups
            .Select(group =>
            {
                var union = new HashSet<uint>();
                foreach (var trigram in group)
                {
                    union.UnionWith(reader.ScanDocIds(TrigramHasher.ComputeHash(trigram[0], trigram[1], trigram[2])));
                }

                return IntersectInput(union, filter, reader);
            })
            .ToList();

        return PostingListIntersect.Intersect(lists);
    }

    // case-variant expansion unions postings of several trigram spellings per group, and the
    // HashSet's insertion order is not docId order — IntersectPair walks two sorted lists, so an
    // unsorted union silently drops every candidate that landed behind a smaller docId
    private static uint[] IntersectInput(HashSet<uint> union, IReadOnlySet<uint>? filter, TrigramIndexReader reader)
    {
        var docIds = union.ToArray();
        Array.Sort(docIds);
        return filter is null
            ? docIds
            : docIds.Where(docId => filter.Contains(reader.GetDoc(docId).FileId)).ToArray();
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

    private static List<uint> FullScan(TrigramIndexReader reader, IReadOnlySet<uint>? filter)
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

    internal static IReadOnlySet<uint>? BuildFilter(MetaStore store, SearchQuery query, IReadOnlySet<uint>? fileScope)
    {
        var kind = DocKindDetector.FilterKind(query.Kind);
        if (query.Repo is null && query.PathGlob is null && query.Language is null && kind is null)
        {
            return fileScope;
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
                if (fileScope is not null && !fileScope.Contains(file.Id))
                {
                    continue;
                }

                if (query.PathGlob is not null && !GlobMatcher.IsMatch(query.PathGlob, file.Path))
                {
                    continue;
                }

                if (query.Language is not null && !string.Equals(file.Language, query.Language, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (kind is not null && DocKindDetector.Detect(file) != kind)
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
        return line.Length <= 200 ? line : line[..200] + "…";
    }

    private static double Score(
        FileDocument file,
        IReadOnlyList<SymbolDocument> symbols,
        SearchQuery query,
        bool caseSensitive,
        string snippet)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var fileName = Path.GetFileName(file.Path);
        var score = fileName.IndexOf(query.Query, comparison) >= 0
            ? RankingWeights.FilenameMatchBonus + RankingWeights.BodyMatchBase
            : RankingWeights.BodyMatchBase;

        if (symbols.Any(s => s.Name.Contains(query.Query, comparison)))
        {
            score = Math.Max(score, RankingWeights.SymbolNameMatchBonus + RankingWeights.BodyMatchBase);
        }
        else if (symbols.Any(s =>
                     (s.Signature?.Contains(query.Query, comparison) ?? false)
                     || (s.DocComment?.Contains(query.Query, comparison) ?? false)))
        {
            score = Math.Max(score, RankingWeights.SignatureDocMatchBonus + RankingWeights.BodyMatchBase);
        }

        if (IsCommentLine(snippet))
        {
            score *= RankingWeights.CommentMatchMultiplier;
        }

        if (fileName.Contains("Tests", StringComparison.Ordinal)
            || fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase))
        {
            score *= RankingWeights.TestFileMultiplier;
        }

        score += Math.Max(0.0, RankingWeights.SizeBonusCap * (1.0 - (double)file.SizeBytes / RankingWeights.SizeBonusReferenceBytes));
        return score;
    }

    private static bool IsCommentLine(string snippet)
    {
        var trimmed = snippet.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal)
               || trimmed.StartsWith("/*", StringComparison.Ordinal)
               || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    internal static string ResolveChunkKey(MetaStore store, uint fileId, int line)
    {
        var start = store.ListChunksByFile(fileId)
            .Where(c => c.StartLine <= line && line <= c.EndLine)
            .Select(c => (int?)c.StartLine)
            .Min();

        return $"{fileId}:{(start ?? line)}";
    }

    private static string ResolveRepoRoot(string workspaceRoot, RepoEntry repo) =>
        Path.GetFullPath(Path.IsPathRooted(repo.Path) ? repo.Path : Path.Combine(workspaceRoot, repo.Path));
}
