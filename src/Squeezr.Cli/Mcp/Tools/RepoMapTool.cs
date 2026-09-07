using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Squeezr.Core.Indexing.Navigation;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp.Tools;

[McpServerToolType]
public sealed class RepoMapTool
{
    private readonly McpWorkspace _workspace;

    public RepoMapTool(McpWorkspace workspace) => _workspace = workspace;

    /// <summary>
    /// Structural map of the indexed repositories: nested directory/file tree rendered as indented
    /// lines with type/member signatures (depth 1 = dirs, 2 = files, 3 = members). Never returns
    /// bodies — use expand_span on coordinates from other tools to read code. Good entry point
    /// for navigating an unfamiliar workspace.
    /// </summary>
    [McpServerTool(Name = "repo_map", Title = "Repository map")]
    public object Build(string? repo = null, int? depth = null, int? maxTokens = null)
    {
        try
        {
            _workspace.RequireWorkspace();
            var root = _workspace.Root;
            var warnings = new List<string>();
            var result = new RepoMapper(root).Build(
                repo,
                depth ?? RepoMapper.DefaultDepth,
                maxTokens ?? RepoMapper.DefaultMaxTokens);

            if (result.Hint is not null)
            {
                warnings.Add(result.Hint);
            }

            var data = result.Lines.Select(l => new MapLineData(l.Depth, l.Text)).ToList();

            return ToolEnvelope.Wrap(data, result.TokensUsed, result.Truncated, warnings, root, staleFiles: null);
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return ToolErrors.From(ex);
        }
    }

    private sealed record MapLineData(
        [property: JsonPropertyName("depth")] int Depth,
        [property: JsonPropertyName("text")] string Text);
}
