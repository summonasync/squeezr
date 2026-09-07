using Spectre.Console.Cli;
using Squeezr.Cli.Mcp;

namespace Squeezr.Cli.Commands;

public sealed class ServeCommand : Command<ServeCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        McpServerHost.RunStdioAsync(Directory.GetCurrentDirectory(), cts.Token).GetAwaiter().GetResult();
        return 0;
    }
}
