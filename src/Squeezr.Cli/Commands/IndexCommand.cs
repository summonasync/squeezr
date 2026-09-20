using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Embeddings;
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
            events = settings.Events ? new IndexEventWriter(Console.Out) : null;

            EmbeddingProviderResolution? providerResolution = null;
            IEmbeddingProvider? embeddings = null;
            IVectorStore? vectorStore = null;
            var rootResolution = WorkspaceRootResolver.ResolveCurrentDirectory();
            if (rootResolution.IsWorkspace)
            {
                var effectiveNoSemantic = IndexingMode.EffectiveNoSemantic(
                    rootResolution.RootPath, settings.NoSemantic);
                if (settings.Reembed && effectiveNoSemantic)
                {
                    Console.Error.WriteLine("warning: --reembed requires semantic indexing; ignored");
                }

                var warnings = EmbeddingRuntimeFactory.TryCreate(
                    rootResolution.RootPath, withProvider: !effectiveNoSemantic,
                    out embeddings, out vectorStore, out providerResolution);
                var providerLine = EmbeddingProviderLogLine.Describe(providerResolution);
                if (providerLine is not null)
                {
                    Console.Error.WriteLine(providerLine);
                }

                foreach (var warning in warnings)
                {
                    Console.Error.WriteLine($"warning: {warning}");
                }
            }

            events?.Started(WorkspaceRootResolver.LaunchDirectory(), provider: providerResolution?.Provider);

            var indexer = new WorkspaceIndexer();
            var workspaceRoot = rootResolution.IsWorkspace
                ? rootResolution.RootPath
                : WorkspaceRootResolver.RequireWorkspaceRoot();

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
                        new IndexProgressRenderer(ctx), embeddings, vectorStore, settings.Reembed,
                        StaleLockWarningSink.ToStderr, providerResolution));
            }
            else
            {
                renderer = settings.Events ? new IndexEventsRenderer(events!) : null;
                result = indexer.Index(
                    workspaceRoot, settings.Repo, settings.Full, settings.NoSemantic,
                    renderer, embeddings, vectorStore, settings.Reembed,
                    StaleLockWarningSink.ToStderr, providerResolution);
            }

            events?.Completed(
                result, (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, renderer?.Symbols,
                provider: providerResolution?.Provider);

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
