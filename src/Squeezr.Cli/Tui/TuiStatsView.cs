using Spectre.Console;
using Squeezr.Cli.Commands;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Tui;

public static class TuiStatsView
{
    public static Task RenderAsync(IAnsiConsole console, string workspaceRoot, CancellationToken cancellationToken = default)
    {
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            console.MarkupLine("[yellow]Workspace not initialized.[/] Run 'squeezr init' to create one.");
            return Task.CompletedTask;
        }

        var summary = QueryTelemetrySummary.Summarize(QueryTelemetry.ReadAll(workspaceRoot), workspaceRoot);
        if (summary.GuardWarnings is { } warnings)
        {
            foreach (var warning in warnings)
            {
                console.MarkupLine($"[yellow]warning:[/] {warning.EscapeMarkup()}");
            }
        }

        StatsCommand.Render(console, summary);
        return Task.CompletedTask;
    }
}
