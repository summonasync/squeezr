using Squeezr.Cli.Commands;
using Squeezr.Cli.Infra;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

public static class CommandError
{
    public static int Report(Exception ex, bool json, string? message = null)
    {
        var text = message ?? ex.Message;
        if (json)
        {
            JsonOutput.WriteError(CodeFor(ex), text);
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
        ServicesException services => services.Code,
        McpUsageException => "invalid_usage",
        InvalidDataException => "invalid_data",
        _ => "internal_error"
    };
}
