using System.Text.Json;
using ModelContextProtocol.Protocol;
using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp;

public static class ToolErrors
{
    public static CallToolResult From(Exception ex, McpWorkspace workspace)
    {
        var code = CommandError.CodeFor(ex);
        var message = ex is WorkspaceException ? ex.Message : $"Internal error: {ex.Message}";
        var cause = CauseFor(code, workspace);
        var nextSteps = NextStepsFor(code, workspace);
        var payload = nextSteps is null
            ? new ToolErrorPayload(code, message)
            : new ToolErrorPayload(code, message, workspace.Root, nextSteps, cause);
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }],
            StructuredContent = JsonSerializer.SerializeToElement(payload)
        };
    }

    private static string? CauseFor(string code, McpWorkspace workspace) => code switch
    {
        WorkspaceException.NotInitializedCode when workspace.SessionOutsideRoot => ToolErrorCause.SessionOutsideRoot,
        WorkspaceException.NotInitializedCode => ToolErrorCause.RootWithoutManifest,
        _ => null
    };

    // only a code with a known remedy carries the advice block; the rest stays message-only
    private static IReadOnlyList<string>? NextStepsFor(string code, McpWorkspace workspace) => code switch
    {
        WorkspaceException.NotInitializedCode when workspace.SessionOutsideRoot => RebindSteps(workspace),
        WorkspaceException.NotInitializedCode => InitializeSteps(workspace),
        WorkspaceException.IndexNotBuiltCode => ["squeezr index"],
        _ => null
    };

    private static IReadOnlyList<string> InitializeSteps(McpWorkspace workspace) =>
    [
        $"cd {ShellPath.Quote(workspace.Root)} && squeezr init",
        $"cd {ShellPath.Quote(workspace.Root)} && squeezr index"
    ];

    private static IReadOnlyList<string> RebindSteps(McpWorkspace workspace) =>
    [
        $"cd {ShellPath.Quote(workspace.SessionDirectory)} && squeezr mcp install --client <client> --scope project",
        $"cd {ShellPath.Quote(workspace.SessionDirectory)} && squeezr serve"
    ];
}
