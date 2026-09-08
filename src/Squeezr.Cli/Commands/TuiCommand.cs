using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Tui;

namespace Squeezr.Cli.Commands;

public sealed class TuiCommand : Command
{
    protected override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            Console.Error.WriteLine("error: 'tui' requires an interactive terminal.");
            return 1;
        }

        return TuiApp.RunInteractiveAsync(Directory.GetCurrentDirectory()).GetAwaiter().GetResult();
    }
}
