using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Tools;

[McpServerToolType]
public sealed class ExpandSpanTool
{
    private readonly McpWorkspace _workspace;

    public ExpandSpanTool(McpWorkspace workspace) => _workspace = workspace;

    /// <summary>
    /// Reveal code around coordinates from code_search/symbol_lookup: the exact lines of the span
    /// plus contextLines of surrounding context, read from the live file on disk (reflects edits
    /// made after indexing). The only tool that returns raw source lines.
    /// </summary>
    [McpServerTool(Name = "expand_span", Title = "Expand span")]
    public object Expand(
        string file,
        int startLine,
        int? endLine = null,
        int? contextLines = null,
        int? maxTokens = null)
    {
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var warnings = new List<string>();
            var result = new SpanExpander(root).Expand(
                file,
                startLine,
                endLine,
                contextLines ?? SpanExpander.DefaultContextLines,
                repo: null,
                maxTokens ?? SpanExpander.DefaultMaxTokens);

            var data = new ExpandData(
                result.Repo,
                result.File,
                result.StartLine,
                result.EndLine,
                result.Before.ToList(),
                result.Span.ToList(),
                result.After.ToList());

            return ToolEnvelope.Wrap(data, result.TokensUsed, false, warnings, root, staleFiles: null);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex);
        }
    }

    private sealed record ExpandData(
        [property: JsonPropertyName("repo")] string Repo,
        [property: JsonPropertyName("file")] string File,
        [property: JsonPropertyName("startLine")] int StartLine,
        [property: JsonPropertyName("endLine")] int EndLine,
        [property: JsonPropertyName("before")] IReadOnlyList<string> Before,
        [property: JsonPropertyName("span")] IReadOnlyList<string> Span,
        [property: JsonPropertyName("after")] IReadOnlyList<string> After);
}
