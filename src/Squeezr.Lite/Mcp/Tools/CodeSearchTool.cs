using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Mcp.Tools;

[McpServerToolType]
public sealed class CodeSearchTool
{
    private const int DefaultMaxTokens = 2000;

    private readonly LiteWorkspace _workspace;
    private readonly LiteToolGate _gate;
    private readonly ClientIdentity _identity;

    public CodeSearchTool(LiteWorkspace workspace, LiteToolGate gate, ClientIdentity identity)
    {
        _workspace = workspace;
        _gate = gate;
        _identity = identity;
    }

    [Description("Exact search over the indexed workspace: matches exact identifiers and domain-worded phrase fragments (\"VectorDimensionMismatchException\" or \"validate config file\") against the trigram index. Returns file:line matches with snippets, never full bodies — expand_span reveals a body around a hit. One hit per file: the best span wins and the file's other spans collapse into alsoMatched, so topK counts distinct files, not spans. Pass prefix (e.g. \"src/\") to scope the search to one directory subtree. kind filters what a hit can be: \"all\" (default) matches everything, \"code\" keeps program sources, \"docs\" keeps prose and config (markdown, text, json, yaml, toml, xml). This lite server has no semantic arm: paraphrase-matching is unavailable and answers come from the exact arms only. Results are refreshed on read: modified files are reindexed inline within a per-call budget or reported in staleFiles, and the freshness block counts what was healed and what was refused.")]
    [McpServerTool(Name = "code_search", Title = "Search code")]
    public Task<object> SearchAsync(
        string query,
        string? repo = null,
        string? prefix = null,
        int? maxTokens = null,
        int? topK = null,
        string? kind = null,
        CancellationToken cancellationToken = default) =>
        _gate.RunAsync(
            ct => SearchCore(query, repo, prefix, maxTokens, topK, kind, ct),
            cancellationToken);

    private async Task<object> SearchCore(
        string query,
        string? repo,
        string? prefix,
        int? maxTokens,
        int? topK,
        string? kind,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            if (!DocKindDetector.TryNormalizeFilter(kind, out var kindFilter))
            {
                throw new WorkspaceException(
                    $"Unknown kind '{kind}' — allowed values: all, code, docs.",
                    WorkspaceException.InvalidQueryCode);
            }

            var root = _workspace.Root;
            var normalizedPrefix = PathPrefix.Normalize(prefix);
            var warnings = new List<string>();
            var rankExclusion = RankExclusion.Load(root);
            warnings.AddRange(rankExclusion.Warnings);

            var engine = new SearchEngine(root, rankExclusion);
            var searchQuery = new SearchQuery(query, false, null, repo, null, null, topK, maxTokens ?? DefaultMaxTokens, normalizedPrefix, kindFilter);
            var response = await engine.SearchHybridAsync(searchQuery, semanticArm: null, cancellationToken);

            warnings.AddRange(response.Warnings);

            var candidates = QueryFreshness.Candidates(response.Matches.Select(m => (m.Repo, m.Path)));
            var freshness = QueryFreshness.Guard(root, candidates, cancellationToken);
            if (freshness.Refreshed.Count > 0)
            {
                response = await engine.SearchHybridAsync(searchQuery, semanticArm: null, cancellationToken);
                foreach (var warning in response.Warnings)
                {
                    if (!warnings.Contains(warning))
                    {
                        warnings.Add(warning);
                    }
                }
            }

            if (response.Truncated)
            {
                warnings.Add(HintText.TruncationLine(
                    response.TokensUsed,
                    maxTokens ?? DefaultMaxTokens,
                    response.Hint?.ForMcp() ?? HintText.RaiseMaxTokensRemedy));
            }

            var data = response.Matches.Select(m => new SearchHitData(
                m.Repo, m.Path, m.Line, m.Score, m.Source, m.Snippet,
                m.AlsoMatched == 0 ? null : m.AlsoMatched)).ToList();

            var namedFiles = data.Select(d => (d.Repo, d.File)).ToList();
            var (cost, unknownBaselineFiles) = CostBaseline.Measure(
                root, namedFiles, response.TokensUsed, !response.Truncated);
            if (WriterNotice.For(root) is { } writerNotice)
            {
                warnings.Add(writerNotice);
            }
            if (unknownBaselineFiles > 0)
            {
                warnings.Add(
                    $"{unknownBaselineFiles} hit file(s) not in the index — excluded from the whole-file baseline");
            }

            var namedCount = namedFiles.Distinct().Count();
            var coverageLine = AnswerFreshness.CoverageWarning(candidates.Count, namedCount);
            if (coverageLine is not null)
            {
                warnings.Add(coverageLine);
            }

            var budgetLine = AnswerFreshness.BudgetWarning(freshness.Unrefreshed.Count);
            if (budgetLine is not null)
            {
                warnings.Add(budgetLine);
            }

            var envelope = ToolEnvelope.Wrap(data, response.TokensUsed, response.Truncated, warnings, root,
                freshness.Stale, cost: cost, freshness: AnswerFreshness.ForContent(
                    candidates.Count, freshness.Refreshed.Count, freshness.Stale.Count,
                    freshness.Stale, [coverageLine, budgetLine], freshness.Unrefreshed.Count));
            QueryTelemetry.Record(root, _workspace.SessionId, "code_search",
                new { query, repo, prefix = normalizedPrefix, maxTokens, topK, kind },
                envelope.PaidTokens, envelope.Truncated, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version, mapped: false);
            return envelope;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex, _workspace);
        }
    }

    private sealed record SearchHitData(
        [property: JsonPropertyName("repo")] string Repo,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("source")] string Source,
        [property: JsonPropertyName("snippet")] string Snippet,
        [property: JsonPropertyName("alsoMatched")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? AlsoMatched);
}
