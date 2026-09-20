using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Tools;

[McpServerToolType]
public sealed class RepoMapTool
{
    private const int GuardCandidateLimit = 50;

    private readonly McpWorkspace _workspace;
    private readonly McpToolGate _gate;
    private readonly ToolResponseCache? _cache;

    public RepoMapTool(McpWorkspace workspace, McpToolGate gate, ToolResponseCache? cache = null)
    {
        _workspace = workspace;
        _gate = gate;
        _cache = cache;
    }

    [Description("Structural map of the indexed repositories: nested directory/file tree rendered as indented lines with type/member signatures (depth 1 = dirs, 2 = files, 3 = members), never bodies. The cheapest first contact with an unfamiliar workspace — orient across repos, dirs and entry points before reading any file. Signatures are refreshed on read: modified files are reindexed inline or reported in staleFiles; expand_span reads code on coordinates from other tools.")]
    [McpServerTool(Name = "repo_map", Title = "Repository map")]
    public object Build(string? repo = null, int? depth = null, int? maxTokens = null) =>
        _gate.Run(() => BuildCore(repo, depth, maxTokens));

    private object BuildCore(string? repo, int? depth, int? maxTokens)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var parameters = Params(repo, depth, maxTokens);
            if (_cache is not null && _cache.TryGet("repo_map", parameters, out var cached))
            {
                var hit = (ToolEnvelope)cached!;
                QueryTelemetry.Record(root, _workspace.SessionId, "repo_map", parameters,
                    hit.TokensUsed, hit.Truncated, ((List<MapLineData>)hit.Data!).Count,
                    sw.ElapsedMilliseconds, cacheHit: true, indexAge: hit.IndexAge);
                return hit;
            }

            var warnings = new List<string>();
            var resolvedDepth = depth ?? RepoMapper.DefaultDepth;
            var resolvedMaxTokens = maxTokens ?? RepoMapper.DefaultMaxTokens;

            var mapper = new RepoMapper(root);
            var result = mapper.Build(repo, resolvedDepth, resolvedMaxTokens);

            var freshness = Guard(root, result);
            if (freshness.Refreshed.Count > 0)
            {
                result = mapper.Build(repo, resolvedDepth, resolvedMaxTokens);
            }

            if (result.Hint is not null)
            {
                warnings.Add(result.Hint);
            }

            var data = result.Lines.Select(l => new MapLineData(l.Depth, l.Text)).ToList();

            var envelope = ToolEnvelope.Wrap(data, result.TokensUsed, result.Truncated, warnings, root, freshness.Stale);
            QueryTelemetry.Record(root, _workspace.SessionId, "repo_map", parameters,
                result.TokensUsed, result.Truncated, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge);
            _cache?.Put("repo_map", parameters, envelope);
            return envelope;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex);
        }
    }

    private static FreshnessResult Guard(string root, RepoMapResult result)
    {
        var candidates = new List<(string Repo, string File)>();
        string? repo = null;
        string? dir = null;
        foreach (var line in result.Lines)
        {
            if (line.Depth == 0)
            {
                repo = line.Text.TrimEnd('/');
                dir = null;
            }
            else if (line.Depth == 1)
            {
                var marker = line.Text.LastIndexOf(" (", StringComparison.Ordinal);
                var name = (marker < 0 ? line.Text : line.Text[..marker]).TrimEnd('/');
                dir = name == "." ? null : name;
            }
            else if (line.Depth == 2 && repo is not null)
            {
                candidates.Add((repo, dir is null ? line.Text : $"{dir}/{line.Text}"));
            }
        }

        return FreshnessGuard.CheckAndRefresh(
            root,
            candidates.Distinct().Take(GuardCandidateLimit).ToList(),
            StaleLockWarningSink.ToStderr);
    }

    private static object Params(string? repo, int? depth, int? maxTokens) =>
        new { repo, depth, maxTokens };

    private sealed record MapLineData(
        [property: JsonPropertyName("depth")] int Depth,
        [property: JsonPropertyName("text")] string Text);
}
