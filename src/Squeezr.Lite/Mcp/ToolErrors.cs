using System.Text.Json;
using ModelContextProtocol.Protocol;
using Squeezr.Lite.Commands;
using Squeezr.Lite.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Lite.Mcp;

public static class ToolErrors
{
    public static CallToolResult From(Exception ex, LiteWorkspace workspace)
    {
        var payload = PayloadFor(ex, workspace);
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = payload.Message }],
            StructuredContent = JsonSerializer.SerializeToElement(payload)
        };
    }

    internal static ToolErrorPayload PayloadFor(Exception ex, LiteWorkspace workspace)
    {
        var code = CommandError.CodeFor(ex);
        var message = ex is WorkspaceException or McpUsageException
            ? ex.Message
            : $"Internal error: {ex.Message}";
        var (cause, nextSteps) = RemedyFor(code, workspace);
        var subject = AnswerSubjectNotice.BodyOf(workspace.Subject);
        return nextSteps is null
            ? new ToolErrorPayload(code, message, Subject: subject)
            : new ToolErrorPayload(code, message, workspace.Root, nextSteps, cause, subject);
    }

    private static (string? Cause, IReadOnlyList<string>? NextSteps) RemedyFor(string code, LiteWorkspace workspace) => code switch
    {
        WorkspaceException.NotInitializedCode when workspace.SessionOutsideRoot => (ToolErrorCause.SessionOutsideRoot, RebindSteps(workspace)),
        WorkspaceException.NotInitializedCode => (ToolErrorCause.RootWithoutManifest, InitializeSteps(workspace)),
        WorkspaceException.IndexNotBuiltCode => (null, [LiteRemedy.AnchorIndex(workspace.Root)]),
        _ => (null, null)
    };

    private static IReadOnlyList<string> InitializeSteps(LiteWorkspace workspace) =>
    [
        LiteRemedy.AnchorInit(workspace.Root),
        LiteRemedy.AnchorIndex(workspace.Root)
    ];

    private static IReadOnlyList<string> RebindSteps(LiteWorkspace workspace) =>
    [
        $"cd '{workspace.SessionDirectory}' && sqzrlite init",
        $"cd '{workspace.SessionDirectory}' && sqzrlite serve"
    ];
}
