using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class AddCommand : Command<AddCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<path>")]
        public string RepoPath { get; set; } = string.Empty;

        [CommandOption("--name")]
        public string? Name { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var manager = new WorkspaceManager(Directory.GetCurrentDirectory());
            var entry = manager.AddRepo(settings.RepoPath, settings.Name);

            if (settings.Json)
            {
                JsonOutput.Write(entry);
                return 0;
            }

            AnsiConsole.MarkupLine($"Added repo [bold]{entry.Name.EscapeMarkup()}[/] at {entry.Path.EscapeMarkup()}");
            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }
}
