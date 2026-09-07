using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Tools;

[McpServerToolType]
public sealed class SymbolLookupTool
{
    private const int GuardCandidateLimit = 50;
    private const string NotFoundPrefix = "No symbol named";

    private readonly McpWorkspace _workspace;
    private readonly McpToolGate _gate;

    public SymbolLookupTool(McpWorkspace workspace, McpToolGate gate)
    {
        _workspace = workspace;
        _gate = gate;
    }

    /// <summary>
    /// Find symbol definitions by name across the indexed workspace: signature, doc comment,
    /// file:span, and body within the token budget (maxTokens, default 300). Not-found returns
    /// an empty result, not an error. Use expand_span (when available) for wider context around a hit.
    /// </summary>
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
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
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
                return ToolEnvelope.Wrap(Array.Empty<object>(), 0, false, warnings, root, staleFiles: null);
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

            return ToolEnvelope.Wrap(data, result.TokensUsed, result.Truncated, warnings, root, freshness.Stale);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex);
        }
    }

    private static FreshnessResult Guard(string root, SymbolLookupResult result) =>
        FreshnessGuard.CheckAndRefresh(
            root,
            result.Matches.Take(GuardCandidateLimit).Select(m => (m.Repo, m.File)).Distinct().ToList());

    private sealed record SymbolHitData(
        [property: JsonPropertyName("repo")] string Repo,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("span")] string Span,
        [property: JsonPropertyName("signature")] string? Signature,
        [property: JsonPropertyName("docComment")] string? DocComment,
        [property: JsonPropertyName("body")] string? Body);
}
