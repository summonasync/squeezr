using System.Text.Json;
using ModelContextProtocol.Protocol;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Mcp;

public static class ToolErrors
{
    public static CallToolResult From(Exception ex, McpWorkspace workspace)
    {
        var payload = PayloadFor(ex, workspace);
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = payload.Message }],
            StructuredContent = JsonSerializer.SerializeToElement(payload)
        };
    }

    internal static ToolErrorPayload PayloadFor(Exception ex, McpWorkspace workspace)
    {
        var code = CommandError.CodeFor(ex);
        var message = ex is WorkspaceException ? ex.Message : $"Internal error: {ex.Message}";
        var (cause, nextSteps) = RemedyFor(code, workspace);
        return nextSteps is null
            ? new ToolErrorPayload(code, message)
            : new ToolErrorPayload(code, message, workspace.Root, nextSteps, cause);
    }

    internal static (string? Cause, IReadOnlyList<string> NextSteps) IndexRemedyFor(string root, string? provider)
    {
        var index = RemedyText.AnchorIndex(root);
        return EmbeddingProviderServices.RequiresManagedService(provider)
            ? (ToolErrorCause.ProviderNeedsServices, [RemedyText.ServicesUp, index])
            : (null, [index]);
    }

    // only a code with a known remedy carries the advice block; the rest stays message-only
    private static (string? Cause, IReadOnlyList<string>? NextSteps) RemedyFor(string code, McpWorkspace workspace) => code switch
    {
        WorkspaceException.NotInitializedCode when workspace.SessionOutsideRoot => (ToolErrorCause.SessionOutsideRoot, RebindSteps(workspace)),
        WorkspaceException.NotInitializedCode => (ToolErrorCause.RootWithoutManifest, InitializeSteps(workspace)),
        WorkspaceException.IndexNotBuiltCode => IndexRemedyFor(workspace.Root, workspace.EmbeddingsProvider),
        _ => (null, null)
    };

    private static IReadOnlyList<string> InitializeSteps(McpWorkspace workspace) =>
    [
        RemedyText.AnchorInit(workspace.Root),
        RemedyText.AnchorIndex(workspace.Root)
    ];

    private static IReadOnlyList<string> RebindSteps(McpWorkspace workspace) =>
    [
        RemedyText.RebindInstall(workspace.SessionDirectory),
        RemedyText.RebindServe(workspace.SessionDirectory)
    ];
}
