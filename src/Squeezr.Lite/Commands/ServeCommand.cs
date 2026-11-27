using Spectre.Console.Cli;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Mcp;

namespace Squeezr.Lite.Commands;

public sealed class ServeCommand : Command<ServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--watch")]
        public bool Watch { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            LiteServerHost.RunStdioAsync(
                WorkspaceRootResolver.LaunchDirectory(),
                cts.Token,
                settings.Watch).GetAwaiter().GetResult();
        }
        catch (WorkspaceException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        return 0;
    }
}
