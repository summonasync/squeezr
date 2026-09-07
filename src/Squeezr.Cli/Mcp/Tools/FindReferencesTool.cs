using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Tools;

[McpServerToolType]
public sealed class FindReferencesTool
{
    private const int GuardCandidateLimit = 50;

    private readonly McpWorkspace _workspace;

    public FindReferencesTool(McpWorkspace workspace) => _workspace = workspace;

    /// <summary>
    /// Find all references to a symbol in the index: every call site as file:line with the
    /// reference kind. Returns a tool error if the symbol is not indexed. Pair with symbol_lookup
    /// to jump from a match to its usages.
    /// </summary>
    [McpServerTool(Name = "find_references", Title = "Find references")]
    public object Find(string symbol, string? repo = null)
    {
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var warnings = new List<string>();
            var lookup = new RefLookup(root);

            var result = lookup.Find(symbol, repo);

            var freshness = Guard(root, result);
            if (freshness.Refreshed.Count > 0)
            {
                result = lookup.Find(symbol, repo);
            }

            var data = result.Groups
                .SelectMany(g => g.Refs.Select(r => new RefData(r.File, r.Line, r.Kind)))
                .ToList();

            var tokensUsed = data.Sum(d => TokenCounter.Estimate($"{d.File}:{d.Line} {d.Kind}"));

            return ToolEnvelope.Wrap(data, tokensUsed, false, warnings, root, freshness.Stale);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex);
        }
    }

    private static FreshnessResult Guard(string root, RefLookupResult result) =>
        FreshnessGuard.CheckAndRefresh(
            root,
            result.Groups
                .SelectMany(g => g.Refs.Select(r => (g.Repo, r.File)).Append((g.Repo, g.File)))
                .Take(GuardCandidateLimit)
                .ToList());

    private sealed record RefData(
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("line")] int Line,
        [property: JsonPropertyName("kind")] string Kind);
}
