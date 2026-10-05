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
            var result = WorkspaceCompactor.Compact(
                WorkspaceRootResolver.RequireWorkspaceRoot(), StaleLockWarningSink.ToStderr);

            foreach (var warning in result.Warnings ?? [])
            {
                Console.Error.WriteLine($"warning: {warning}");
            }

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

                AnsiConsole.MarkupLine(
                    $"[bold]meta.db[/]: {FormatKb(result.StoreSizeBeforeBytes)} → {FormatKb(result.StoreSizeAfterBytes)}");
                if (PurgeLine(result) is { } purgeLine)
                {
                    AnsiConsole.MarkupLine($"[grey]{purgeLine.EscapeMarkup()}[/]");
                }
            }

            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or InvalidDataException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    internal static string? PurgeLine(CompactResult result)
    {
        if (result.PathsPurged == 0 && result.ReapJournalRedacted == 0 && result.TelemetryValuesRedacted == 0)
        {
            return null;
        }

        var parts = new List<string>();
        if (result.PathsPurged > 0)
        {
            parts.Add($"{result.PathsPurged} path(s) purged, {result.RowsPurged} row(s) removed");
        }

        if (result.ReapJournalRedacted > 0)
        {
            parts.Add($"{result.ReapJournalRedacted} reap journal name(s) redacted");
        }

        if (result.TelemetryValuesRedacted > 0)
        {
            parts.Add($"{result.TelemetryValuesRedacted} recorded parameter value(s) redacted");
        }

        if (result.PointsQueued > 0)
        {
            parts.Add($"{result.PointsQueued} point cleanup(s) queued");
        }

        return "secret guard: " + string.Join(" · ", parts);
    }

    private static string FormatKb(long bytes) => $"{bytes / 1024.0:F1} KB";
}
