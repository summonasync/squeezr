using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

public static class CommandError
{
    public static int Report(Exception ex, bool json)
    {
        if (json)
        {
            JsonOutput.WriteError(CodeFor(ex), ex.Message);
        }
        else
        {
            Console.Error.WriteLine($"error: {ex.Message}");
        }

        return 1;
    }

    public static string CodeFor(Exception ex) => ex switch
    {
        WorkspaceException workspace => workspace.Code,
        InvalidDataException => "invalid_data",
        _ => "internal_error"
    };
}
