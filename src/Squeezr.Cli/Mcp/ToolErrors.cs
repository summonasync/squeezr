using System.Text.Json;
using ModelContextProtocol.Protocol;
using Squeezr.Cli.Commands;
using Squeezr.Semantic.Embeddings;
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

        // a client-fault code (invalid_usage) must not read as retryable: only unknown failures
        // carry the internal prefix, usage errors speak for themselves
        var message = ex is WorkspaceException or McpUsageException
            ? ex.Message
            : $"Internal error: {ex.Message}";
        var (cause, nextSteps) = RemedyFor(code, workspace);
        // A refusal is an answer about a tree, and the gate that stamps envelopes skips this shape, so the
        // naming happens here: "No symbol named 'X' in the index" without the subject is a refusal about
        // nobody, and in a crossed session the reader has two candidate indices to guess between.
        var subject = AnswerSubjectNotice.BodyOf(workspace.Subject);
        return nextSteps is null
            ? new ToolErrorPayload(code, message, Subject: subject)
            : new ToolErrorPayload(code, message, workspace.Root, nextSteps, cause, subject);
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
