using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Tools;

[McpServerToolType]
public sealed class ExpandSpanTool
{
    private readonly McpWorkspace _workspace;
    private readonly McpToolGate _gate;

    public ExpandSpanTool(McpWorkspace workspace, McpToolGate gate)
    {
        _workspace = workspace;
        _gate = gate;
    }

    [Description("Reveal code around coordinates from code_search/symbol_lookup: the exact lines of the span plus contextLines of surrounding context, read from the live file on disk (reflects edits made after indexing). The only tool that returns raw source lines. When truncated is true, context (before/after) may be partially filled — the span itself has priority.")]
    [McpServerTool(Name = "expand_span", Title = "Expand span")]
    public object Expand(
        string file,
        int startLine,
        int? endLine = null,
        int? contextLines = null,
        int? maxTokens = null) =>
        _gate.Run(() => ExpandCore(file, startLine, endLine, contextLines, maxTokens));

    private object ExpandCore(
        string file,
        int startLine,
        int? endLine,
        int? contextLines,
        int? maxTokens)
    {
        var sw = Stopwatch.StartNew();
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

            var envelope = ToolEnvelope.Wrap(data, result.TokensUsed, result.Truncated, warnings, root, staleFiles: null);
            QueryTelemetry.Record(root, _workspace.SessionId, "expand_span",
                new { file, startLine, endLine, contextLines },
                result.TokensUsed, result.Truncated, data.Span.Count,
                sw.ElapsedMilliseconds, indexAge: envelope.IndexAge);
            return envelope;
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
