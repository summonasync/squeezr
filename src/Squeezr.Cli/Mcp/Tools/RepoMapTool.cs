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
    private readonly ClientIdentity _identity;

    public RepoMapTool(McpWorkspace workspace, McpToolGate gate, ToolResponseCache? cache, ClientIdentity identity)
    {
        _workspace = workspace;
        _gate = gate;
        _cache = cache;
        _identity = identity;
    }

    [Description("Structural map of the indexed repositories: nested directory/file tree rendered as indented lines with type/member signatures (depth 1 = dirs, 2 = files, 3 = members), never bodies. The cheapest first contact with an unfamiliar workspace — orient across repos, dirs and entry points before reading any file. Pass prefix (e.g. \"src/Squeezr.Cli/\") to render only that subtree with prefix-relative directory labels — depth keeps its structural meaning. Signatures are refreshed on read: modified files are reindexed inline or reported in staleFiles; expand_span reads code on coordinates from other tools.")]
    [McpServerTool(Name = "repo_map", Title = "Repository map")]
    public object Build(string? repo = null, string? prefix = null, int? depth = null, int? maxTokens = null) =>
        _gate.Run(() => BuildCore(repo, prefix, depth, maxTokens));

    private object BuildCore(string? repo, string? prefix, int? depth, int? maxTokens)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var normalizedPrefix = PathPrefix.Normalize(prefix);
            var parameters = Params(repo, normalizedPrefix, depth, maxTokens, SecretGuardReader.Load(root).ExtraPatterns);
            if (_cache is not null && _cache.TryGet("repo_map", parameters, out var cached))
            {
                var hit = (ToolEnvelope)cached!;
                QueryTelemetry.Record(root, _workspace.SessionId, "repo_map", parameters,
                    hit.TokensUsed, hit.Truncated, ((List<MapLineData>)hit.Data!).Count,
                    sw.ElapsedMilliseconds, cacheHit: true, indexAge: hit.IndexAge,
                    client: _identity.Name ?? "unknown", clientVersion: _identity.Version);
                return hit;
            }

            var warnings = new List<string>();
            var resolvedDepth = depth ?? RepoMapper.DefaultDepth;
            var resolvedMaxTokens = maxTokens ?? RepoMapper.DefaultMaxTokens;

            var mapper = new RepoMapper(root);
            var result = mapper.Build(repo, resolvedDepth, resolvedMaxTokens, normalizedPrefix);

            var freshness = Guard(root, result);
            if (freshness.Refreshed.Count > 0)
            {
                result = mapper.Build(repo, resolvedDepth, resolvedMaxTokens, normalizedPrefix);
            }

            if (result.GuardWarnings is { } withheld)
            {
                warnings.AddRange(withheld);
            }

            if (result.Truncated)
            {
                warnings.Add(HintText.TruncationLine(
                    result.TokensUsed,
                    resolvedMaxTokens,
                    result.Hint?.ForMcp() ?? "raise maxTokens"));
            }

            var data = result.Lines.Select(l => new MapLineData(l.Depth, l.Text)).ToList();

            var envelope = ToolEnvelope.Wrap(data, result.TokensUsed, result.Truncated, warnings, root, freshness.Stale);
            var invalidated = _cache?.TakeInvalidationWarning();
            QueryTelemetry.Record(root, _workspace.SessionId, "repo_map", parameters,
                result.TokensUsed, result.Truncated, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version,
                cacheInvalidated: invalidated is not null);
            _cache?.Put("repo_map", parameters, envelope);
            return envelope.WithWarning(invalidated);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex, _workspace);
        }
    }

    private static FreshnessResult Guard(string root, RepoMapResult result)
    {
        // dir labels render relative to the prefix, so candidates need it back — in stored casing
        var prefix = result.ScopedPrefix;
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
                var relative = dir is null ? line.Text : $"{dir}/{line.Text}";
                candidates.Add((repo, prefix is null ? relative : prefix + relative));
            }
        }

        return FreshnessGuard.CheckAndRefresh(
            root,
            candidates.Distinct().Take(GuardCandidateLimit).ToList(),
            StaleLockWarningSink.ToStderr);
    }

    private static object Params(
        string? repo,
        string? prefix,
        int? depth,
        int? maxTokens,
        IReadOnlyList<string> secretGuardExtra) =>
        new { repo, prefix, depth, maxTokens, secretGuardExtra };

    private sealed record MapLineData(
        [property: JsonPropertyName("depth")] int Depth,
        [property: JsonPropertyName("text")] string Text);
}
