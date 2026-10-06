using System.Text.Json;
using ModelContextProtocol.Protocol;
using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp;

public static class ToolErrors
{
    public static CallToolResult From(Exception ex, string? rootPath = null)
    {
        var code = CommandError.CodeFor(ex);
        var message = ex is WorkspaceException ? ex.Message : $"Internal error: {ex.Message}";
        var nextSteps = NextStepsFor(code);
        var payload = nextSteps is null
            ? new ToolErrorPayload(code, message)
            : new ToolErrorPayload(code, message, rootPath, nextSteps);
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }],
            StructuredContent = JsonSerializer.SerializeToElement(payload)
        };
    }

    // only a code with a known remedy carries the advice block; the rest stays message-only
    private static IReadOnlyList<string>? NextStepsFor(string code) => code switch
    {
        WorkspaceException.NotInitializedCode => ["squeezr init", "squeezr index"],
        WorkspaceException.IndexNotBuiltCode => ["squeezr index"],
        _ => null
    };
}
