using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class CompactCommand : Command<CompactCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            var result = WorkspaceCompactor.Compact(Directory.GetCurrentDirectory());

            if (settings.Json)
            {
                JsonOutput.Write(result);
            }
            else
            {
                AnsiConsole.MarkupLine(
                    $"[bold]trigram.idx[/]: {FormatKb(result.SizeBeforeBytes)} → {FormatKb(result.SizeAfterBytes)} " +
                    $"({result.SegmentsDropped} segments dropped)");
                if (result.RebuiltFromSources)
                {
                    AnsiConsole.MarkupLine("[grey]legacy index rebuilt from current file contents[/]");
                }
            }

            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or InvalidDataException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            if (settings.Json)
            {
                JsonOutput.Write(new { Error = ex.Message });
            }

            return 1;
        }
    }

    private static string FormatKb(long bytes) => $"{bytes / 1024.0:F1} KB";
}
