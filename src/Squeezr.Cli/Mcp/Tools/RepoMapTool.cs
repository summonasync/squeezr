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
    private readonly McpWorkspace _workspace;
    private readonly McpToolGate _gate;
    private readonly ToolResponseCache? _cache;
    private readonly ClientIdentity _identity;

    // the render stays a plain RepoMapper in production; the seam exists so a caller can count the maps one
    // answer builds — the double render this shape replaced was invisible from the outside
    private readonly Func<string, RepoMapper> _mapper;

    public RepoMapTool(
        McpWorkspace workspace,
        McpToolGate gate,
        ToolResponseCache? cache,
        ClientIdentity identity,
        Func<string, RepoMapper>? mapperFactory = null)
    {
        _workspace = workspace;
        _gate = gate;
        _cache = cache;
        _identity = identity;
        _mapper = mapperFactory ?? (root => new RepoMapper(root));
    }

    [Description("Structural map of the indexed repositories: nested directory/file tree rendered as indented lines with type/member signatures (depth 1 = dirs, 2 = files, 3 = members), never bodies. The cheapest first contact with an unfamiliar workspace — orient across repos, dirs and entry points before reading any file. Pass prefix (e.g. \"src/Squeezr.Cli/\") to render only that subtree with prefix-relative directory labels — depth keeps its structural meaning. Lines come from the index: one structural pass over the indexed rows reports files missing from disk or changed by size or mtime in staleFiles, and nothing is reindexed on this call — expand_span reads code on coordinates from other tools.")]
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

            var result = _mapper(root).Build(repo, resolvedDepth, resolvedMaxTokens, normalizedPrefix);

            // one structural pass over the rows this scope renders claims, against the disk as it is now;
            // the answer is not rebuilt behind it, so a dirty file costs a stat and not a second render
            var freshness = StructureFreshnessCheck.Check(root, result.Repos, result.ScopedPrefix);

            if (result.GuardWarnings is { } withheld)
            {
                warnings.AddRange(withheld);
            }

            if (result.Truncated)
            {
                warnings.Add(HintText.TruncationLine(
                    result.TokensUsed,
                    resolvedMaxTokens,
                    result.Hint?.ForMcp() ?? HintText.RaiseMaxTokensRemedy));
            }

            var data = result.Lines.Select(l => new MapLineData(l.Depth, l.Text)).ToList();

            warnings.Add(
                "repo_map renders the tree structurally — per-file freshness is not attached to map lines; " +
                "code_search or code_pack carries the verdict for a file you are about to open");

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
