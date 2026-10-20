using Spectre.Console.Cli;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class ServeCommand : Command<ServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--watch")]
        public bool Watch { get; set; }

        [CommandOption("--events")]
        public bool Events { get; set; }

        [CommandOption("--no-embed-watch")]
        public bool NoEmbedWatch { get; set; }
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
            McpServerHost.RunStdioAsync(
                WorkspaceRootResolver.LaunchDirectory(),
                cts.Token, settings.Watch, settings.Events, !settings.NoEmbedWatch).GetAwaiter().GetResult();
        }
        catch (WorkspaceException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }

        return 0;
    }
}
