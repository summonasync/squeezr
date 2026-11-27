using System.Text.Json;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Infra;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Cli.Output;

public static class CommandError
{
    public static int Report(Exception ex, bool json, string? message = null)
    {
        var text = message ?? ex.Message;
        if (json)
        {
            JsonOutput.WriteError(CodeFor(ex), text, RemovedPathsOf(ex), RemedyStepsFor(ex));
        }
        else
        {
            Console.Error.WriteLine($"error: {text}");
        }

        return 1;
    }

    public static string CodeFor(Exception ex) => ex switch
    {
        WorkspaceException workspace => workspace.Code,
        WorkspaceStoreRemovalException => WorkspaceException.DefaultCode,
        WorkspaceStoreRemovalUnverifiedException => WorkspaceException.DefaultCode,
        ServicesException services => services.Code,
        McpUsageException => "invalid_usage",
        ArgumentException => "invalid_usage",
        InvalidDataException => "invalid_data",
        IOException or UnauthorizedAccessException or JsonException => WorkspaceException.StorageUnavailableCode,
        _ => "internal_error"
    };

    // The half-applied removal is the one failure where the paths that did disappear are the report:
    // without them the reader has to guess whether anything went.
    private static IReadOnlyList<string>? RemovedPathsOf(Exception ex) =>
        ex is WorkspaceStoreRemovalUnverifiedException unverified ? unverified.RemovedStoreDirs : null;

    // The CLI reader stands in the workspace the remedy anchors to, so the steps are composed for the
    // process working directory; the provider fact is read once here, the same no-guessing rule as serve.
    private static IReadOnlyList<string>? RemedyStepsFor(Exception ex)
    {
        if (ex is not WorkspaceException workspace)
        {
            return null;
        }

        var root = Directory.GetCurrentDirectory();
        return workspace.Code switch
        {
            WorkspaceException.NotInitializedCode => [RemedyText.AnchorInit(root), RemedyText.AnchorIndex(root)],
            WorkspaceException.IndexNotBuiltCode => ToolErrors
                .IndexRemedyFor(root, StartupConfig.TryRead(root)?.Embeddings.Provider).NextSteps,
            _ => null
        };
    }
}
