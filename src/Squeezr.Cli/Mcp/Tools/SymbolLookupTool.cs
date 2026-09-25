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
public sealed class SymbolLookupTool
{
    private const int GuardCandidateLimit = 50;
    private const string NotFoundPrefix = "No symbol named";

    private readonly McpWorkspace _workspace;
    private readonly McpToolGate _gate;
    private readonly ToolResponseCache? _cache;
    private readonly ClientIdentity _identity;

    public SymbolLookupTool(McpWorkspace workspace, McpToolGate gate, ToolResponseCache? cache, ClientIdentity identity)
    {
        _workspace = workspace;
        _gate = gate;
        _cache = cache;
        _identity = identity;
    }

    [Description("Look up an exact symbol by name across the indexed workspace: signature, doc comment, file:span, and body within the token budget (maxTokens, default 300) — the method you named, not the whole file. Not-found returns an empty result, not an error; expand_span widens the context around a hit.")]
    [McpServerTool(Name = "symbol_lookup", Title = "Look up symbol")]
    public object Find(
        string name,
        string? kind = null,
        string? repo = null,
        int? maxTokens = null) =>
        _gate.Run(() => FindCore(name, kind, repo, maxTokens));

    private object FindCore(
        string name,
        string? kind,
        string? repo,
        int? maxTokens)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var parameters = Params(name, kind, repo);
            if (_cache is not null && _cache.TryGet("symbol_lookup", parameters, out var cached))
            {
                var hit = (ToolEnvelope)cached!;
                QueryTelemetry.Record(root, _workspace.SessionId, "symbol_lookup", parameters,
                    hit.TokensUsed, hit.Truncated, ((List<SymbolHitData>)hit.Data!).Count,
                    sw.ElapsedMilliseconds, cacheHit: true, indexAge: hit.IndexAge,
                    client: _identity.Name ?? "unknown", clientVersion: _identity.Version);
                return hit;
            }

            var warnings = new List<string>();
            var budget = maxTokens ?? SymbolLookup.DefaultMaxTokens;
            var lookup = new SymbolLookup(root);

            SymbolLookupResult result;
            try
            {
                result = lookup.Find(name, repo, kind, budget, body: true);
            }
            catch (WorkspaceException ex) when (ex.Message.StartsWith(NotFoundPrefix, StringComparison.Ordinal))
            {
                var miss = ToolEnvelope.Wrap(Array.Empty<object>(), 0, false, warnings, root, staleFiles: null);
                QueryTelemetry.Record(root, _workspace.SessionId, "symbol_lookup", parameters,
                    miss.TokensUsed, miss.Truncated, 0,
                    sw.ElapsedMilliseconds, indexAge: miss.IndexAge,
                    client: _identity.Name ?? "unknown", clientVersion: _identity.Version);
                return miss;
            }

            var freshness = Guard(root, result);
            if (freshness.Refreshed.Count > 0)
            {
                result = lookup.Find(name, repo, kind, budget, body: true);
            }

            if (result.Hint is not null)
            {
                warnings.Add(result.Hint);
            }

            var data = result.Matches.Select(m => new SymbolHitData(
                m.Repo, m.File, $"{m.StartLine}-{m.EndLine}", m.Signature, m.DocComment, m.Body)).ToList();

            var envelope = ToolEnvelope.Wrap(data, result.TokensUsed, result.Truncated, warnings, root, freshness.Stale);
            QueryTelemetry.Record(root, _workspace.SessionId, "symbol_lookup", parameters,
                result.TokensUsed, result.Truncated, data.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge,
                client: _identity.Name ?? "unknown", clientVersion: _identity.Version);
            _cache?.Put("symbol_lookup", parameters, envelope);
            return envelope;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex);
        }
    }

    private static FreshnessResult Guard(string root, SymbolLookupResult result) =>
        FreshnessGuard.CheckAndRefresh(
            root,
            result.Matches.Take(GuardCandidateLimit).Select(m => (m.Repo, m.File)).Distinct().ToList(),
            StaleLockWarningSink.ToStderr);

    private static object Params(string name, string? kind, string? repo) =>
        new { name, kind, repo };

    private sealed record SymbolHitData(
        [property: JsonPropertyName("repo")] string Repo,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("span")] string Span,
        [property: JsonPropertyName("signature")] string? Signature,
        [property: JsonPropertyName("docComment")] string? DocComment,
        [property: JsonPropertyName("body")] string? Body);
}
