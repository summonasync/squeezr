using Squeezr.Core.Indexing.Trigram;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Query;

public static class IdentifierTokenLeg
{
    private const int MaxTokens = 8;
    private const int MaxCandidatesPerToken = 80;
    private const double DfPercentCap = 0.05;
    private const int DfAbsoluteCap = 200;

    internal sealed class ReadCounter
    {
        public int CandidateReads { get; private set; }
        public int TokensSelected { get; private set; }
        public int FileReads { get; private set; }
        public int MaxReadsPerFile { get; private set; }
        private readonly Dictionary<uint, int> _readsByFile = new();

        public void AddCandidateReads(int count) => CandidateReads += count;
        public void SetTokensSelected(int count) => TokensSelected = count;
        public void RecordFileRead(uint fileId)
        {
            FileReads++;
            _readsByFile.TryGetValue(fileId, out var prev);
            _readsByFile[fileId] = prev + 1;
            if (prev + 1 > MaxReadsPerFile) MaxReadsPerFile = prev + 1;
        }
    }

    public static List<(SearchMatch Match, string? Key)> Execute(
        string workspaceRoot,
        SearchQuery query,
        IReadOnlySet<uint>? fileScope) =>
        Execute(workspaceRoot, query, fileScope, null);

    internal static List<(SearchMatch Match, string? Key)> Execute(
        string workspaceRoot,
        SearchQuery query,
        IReadOnlySet<uint>? fileScope,
        ReadCounter? counter)
    {
        var tokens = Tokenize(query.Query);
        // a lone token is the phrase arm's own literal — the leg only earns its keep when no whole
        // phrase can match, so single-token queries stay byte-identical to the pre-leg pipeline
        if (tokens.Count < 2)
        {
            return [];
        }

        var indexPath = SqueezrPaths.TrigramIndexPath(workspaceRoot);
        if (!File.Exists(indexPath))
        {
            return [];
        }

        using var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot));
        // fileScope carries prefix/rankExclude only; repo/language/pathGlob ride the query, so the
        // leg folds them through the phrase arm's filter instead of inventing its own semantics
        var filter = ExactSearchPlan.BuildFilter(store, query, fileScope);
        var reader = new TrigramIndexReader(indexPath);

        var manifest = WorkspaceManifestFile.Load(workspaceRoot);
        var reposById = store.ListRepos().ToDictionary(r => r.Id);
        var entriesByName = manifest.Repos.ToDictionary(r => r.Name, StringComparer.OrdinalIgnoreCase);

        var totalDocs = (int)reader.DocCount;
        if (totalDocs == 0)
        {
            return [];
        }

        var dfCap = Math.Max(1, (int)Math.Min(totalDocs * DfPercentCap, DfAbsoluteCap));
        var discriminants = SelectDiscriminants(tokens, reader, filter, dfCap);
        counter?.SetTokensSelected(discriminants.Count);

        if (discriminants.Count == 0)
        {
            return [];
        }

        var fileHits = new Dictionary<uint, FileAccumulator>();
        var contentCache = new Dictionary<uint, string>();

        foreach (var (token, df, candidateDocIds) in discriminants)
        {
            var taken = candidateDocIds.Length > MaxCandidatesPerToken
                ? candidateDocIds[..MaxCandidatesPerToken]
                : candidateDocIds;
            counter?.AddCandidateReads(taken.Length);

            foreach (var docId in taken)
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

                if (!contentCache.TryGetValue(fileId, out var content))
                {
                    var fullPath = Path.Combine(
                        ResolveRepoRoot(workspaceRoot, entry),
                        file.Path.Replace('/', Path.DirectorySeparatorChar));

                    try
                    {
                        content = File.ReadAllText(fullPath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        continue;
                    }

                    contentCache[fileId] = content;
                    counter?.RecordFileRead(fileId);
                }

                var index = IsCaseSensitive(token)
                    ? content.IndexOf(token, StringComparison.Ordinal)
                    : content.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                var line = content.AsSpan(0, index).Count('\n') + 1;
                var snippet = ExtractLine(content, index);

                if (!fileHits.TryGetValue(fileId, out var acc))
                {
                    acc = new FileAccumulator(file, repoDoc.Name, line, snippet);
                    fileHits[fileId] = acc;
                }

                acc.AddToken(token, df);
            }
        }

        var results = new List<(SearchMatch Match, string? Key)>();
        foreach (var acc in fileHits.Values)
        {
            var score = acc.Score;
            var key = ExactSearchPlan.ResolveChunkKey(store, acc.File.Id, acc.FirstLine);

            results.Add((new SearchMatch(
                acc.RepoName,
                acc.File.Path,
                acc.FirstLine,
                score,
                acc.Snippet)
            {
                Source = "exact"
            }, key));
        }

        return results
            .OrderByDescending(hit => hit.Match.Score)
            .ThenBy(hit => hit.Match.Path, StringComparer.Ordinal)
            .ToList();
    }

    internal static List<string> Tokenize(string query)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= query.Length; i++)
        {
            var isAlnum = i < query.Length && char.IsLetterOrDigit(query[i]);
            if (isAlnum && start < 0)
            {
                start = i;
            }
            else if (!isAlnum && start >= 0)
            {
                var len = i - start;
                if (len >= 3)
                {
                    var token = query[start..i];
                    if (!IsPureDigit(token))
                    {
                        tokens.Add(token);
                    }
                }
                start = -1;
            }
        }
        return tokens;
    }

    private static bool IsPureDigit(string s)
    {
        foreach (var c in s)
        {
            if (!char.IsDigit(c)) return false;
        }
        return true;
    }

    private static bool IsCodeShaped(string token)
    {
        if (token.Contains('_') || token.Contains('$'))
        {
            return true;
        }

        if (token.Length >= 12)
        {
            return true;
        }

        var hasUpper = false;
        var hasLower = false;
        foreach (var c in token)
        {
            if (char.IsUpper(c)) hasUpper = true;
            if (char.IsLower(c)) hasLower = true;
            if (hasUpper && hasLower) return true;
        }

        return false;
    }

    private static List<(string Token, int Df, uint[] Candidates)> SelectDiscriminants(
        List<string> tokens,
        TrigramIndexReader reader,
        IReadOnlySet<uint>? filter,
        int dfCap)
    {
        var scored = new List<(string Token, int Df, uint[] Candidates, double Priority)>();

        foreach (var token in tokens)
        {
            var candidateDocIds = GetCandidateDocIds(reader, token, filter);
            var df = candidateDocIds.Length;

            // a token with no candidates carries no information and would out-rank every real one
            if (df == 0 || (df > dfCap && !IsCodeShaped(token)))
            {
                continue;
            }

            var priority = -Math.Log(1 + df) + token.Length * 0.1;
            scored.Add((token, df, candidateDocIds, priority));
        }

        return scored
            .OrderByDescending(x => x.Priority)
            .Take(MaxTokens)
            .Select(x => (x.Token, x.Df, x.Candidates))
            .ToList();
    }

    private static uint[] GetCandidateDocIds(
        TrigramIndexReader reader,
        string token,
        IReadOnlySet<uint>? filter)
    {
        if (token.Length < 3)
        {
            return [];
        }

        // the cap that bounds case expansion on a long literal degrades this to case-sensitive
        // candidates; folded trigrams in the index itself are an index-format change (no owner yet)
        var groups = ExactSearchPlan.LiteralGroups(token, IsCaseSensitive(token), out _);
        return ExactSearchPlan.ResolveCandidates(reader, groups, filter);
    }

    private static bool IsCaseSensitive(string token) => token != token.ToLowerInvariant();

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

    private static string ResolveRepoRoot(string workspaceRoot, RepoEntry repo) =>
        Path.GetFullPath(Path.IsPathRooted(repo.Path) ? repo.Path : Path.Combine(workspaceRoot, repo.Path));

    private sealed class FileAccumulator
    {
        private readonly HashSet<string> _tokens = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _tokenDfs = new(StringComparer.Ordinal);

        public FileAccumulator(FileDocument file, string repoName, int firstLine, string snippet)
        {
            File = file;
            RepoName = repoName;
            FirstLine = firstLine;
            Snippet = snippet;
        }

        public FileDocument File { get; }
        public string RepoName { get; }
        public int FirstLine { get; }
        public string Snippet { get; }

        public double Score
        {
            get
            {
                var sum = 0.0;
                foreach (var df in _tokenDfs.Values)
                {
                    sum += 1.0 / (1.0 + Math.Log(df));
                }
                sum += 0.5 * Math.Max(0, _tokens.Count - 1);
                return sum;
            }
        }

        public void AddToken(string token, int df)
        {
            if (_tokens.Add(token))
            {
                _tokenDfs[token] = df;
            }
        }
    }
}
