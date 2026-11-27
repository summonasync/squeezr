using System.Text.Json;
using Squeezr.Lite.Commands;
using Squeezr.Core.Workspace;

namespace Squeezr.Lite.Output;

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
        McpUsageException => "invalid_usage",
        ArgumentException => "invalid_usage",
        InvalidDataException => "invalid_data",
        IOException or UnauthorizedAccessException or JsonException => WorkspaceException.StorageUnavailableCode,
        _ => "internal_error"
    };

    private static IReadOnlyList<string>? RemovedPathsOf(Exception ex) =>
        ex is WorkspaceStoreRemovalUnverifiedException unverified ? unverified.RemovedStoreDirs : null;

    private static IReadOnlyList<string>? RemedyStepsFor(Exception ex)
    {
        if (ex is not WorkspaceException workspace)
        {
            return null;
        }

        var root = Directory.GetCurrentDirectory();
        return workspace.Code switch
        {
            WorkspaceException.NotInitializedCode => [LiteRemedy.AnchorInit(root), LiteRemedy.AnchorIndex(root)],
            WorkspaceException.IndexNotBuiltCode => [LiteRemedy.AnchorIndex(root)],
            _ => null
        };
    }
}
