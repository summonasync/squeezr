using ModelContextProtocol.Protocol;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp;

public static class ToolErrors
{
    public static CallToolResult From(Exception ex)
    {
        var message = ex is WorkspaceException ? ex.Message : $"Internal error: {ex.Message}";
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }]
        };
    }
}
