using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class IndexCommand : Command<IndexCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--repo")]
        public string? Repo { get; set; }

        [CommandOption("--full")]
        public bool Full { get; set; }

        [CommandOption("--no-semantic")]
        public bool NoSemantic { get; set; }

        [CommandOption("--reembed")]
        public bool Reembed { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }

        [CommandOption("--events")]
        public bool Events { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        IndexEventWriter? events = null;
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var indexer = new WorkspaceIndexer();
            var workspaceRoot = Directory.GetCurrentDirectory();

            var warnings = EmbeddingRuntimeFactory.TryCreate(
                workspaceRoot, withProvider: true, out var embeddings, out var vectorStore);
            foreach (var warning in warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }

            events = settings.Events ? new IndexEventWriter(Console.Out) : null;
            events?.Started(workspaceRoot);

            IndexEventsRenderer? renderer = null;
            IndexResult result;
            if (!settings.Json && !settings.Events && AnsiConsole.Profile.Capabilities.Interactive)
            {
                result = AnsiConsole.Progress()
                    .AutoClear(true)
                    .Columns(
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new SpinnerColumn())
                    .Start(ctx => indexer.Index(
                        workspaceRoot, settings.Repo, settings.Full, settings.NoSemantic,
                        new IndexProgressRenderer(ctx), embeddings, vectorStore, settings.Reembed));
            }
            else
            {
                renderer = settings.Events ? new IndexEventsRenderer(events!) : null;
                result = indexer.Index(
                    workspaceRoot, settings.Repo, settings.Full, settings.NoSemantic,
                    renderer, embeddings, vectorStore, settings.Reembed);
            }

            events?.Completed(
                result, (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, renderer?.Symbols);

            if (settings.Json)
            {
                JsonOutput.Write(result);
            }
            else if (!settings.Events)
            {
                foreach (var repo in result.Repos)
                {
                    AnsiConsole.MarkupLine(
                        $"[bold]{repo.Name}[/]: +{repo.Added} ~{repo.Modified} -{repo.Deleted} " +
                        $"(skipped: {repo.Skipped}, files: {repo.Files})");
                }

                foreach (var warning in result.Warnings)
                {
                    Console.Error.WriteLine($"warning: {warning}");
                }
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            events?.Error(CommandError.CodeFor(ex), ex.Message);
            return CommandError.Report(ex, settings.Json || settings.Events);
        }
    }
}
