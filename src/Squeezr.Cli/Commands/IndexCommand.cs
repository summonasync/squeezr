using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Semantic.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Indexing;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Indexing.Watch;

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
            IIndexVectorPhase? vectorPhase = null;
            string? embeddingsUnavailable = null;
            var rootResolution = WorkspaceRootResolver.ResolveCurrentDirectory();
            var effectiveNoSemantic = rootResolution.IsWorkspace
                ? IndexingMode.EffectiveNoSemantic(rootResolution.RootPath, settings.NoSemantic)
                : settings.NoSemantic;
            if (rootResolution.IsWorkspace)
            {
                if (settings.Reembed && effectiveNoSemantic)
                {
                    Console.Error.WriteLine("warning: --reembed requires semantic indexing; ignored");
                }

                var warnings = EmbeddingRuntimeFactory.TryCreate(
                    rootResolution.RootPath, withProvider: !effectiveNoSemantic,
                    out embeddings, out vectorStore, out providerResolution);
                if (embeddings is not null && vectorStore is not null)
                {
                    vectorPhase = new WatchEmbeddingRuntime(embeddings, vectorStore, providerResolution);
                }

                var providerLine = EmbeddingProviderLogLine.Describe(embeddings is null ? null : providerResolution);
                if (providerLine is not null)
                {
                    Console.Error.WriteLine(providerLine);
                }

                foreach (var warning in warnings)
                {
                    Console.Error.WriteLine($"warning: {warning}");
                }

                if (embeddings is null && !effectiveNoSemantic)
                {
                    embeddingsUnavailable = warnings.Count > 0
                        ? $"provider unavailable: {string.Join("; ", warnings)}"
                        : "provider unavailable";
                }
            }

            events?.Started(
                WorkspaceRootResolver.LaunchDirectory(),
                provider: embeddings is null ? null : providerResolution?.Provider);

            if (events is not null && embeddingsUnavailable is not null)
            {
                events.Warning(JsonWarningBody.EmbeddingsUnavailable, embeddingsUnavailable);
            }

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
                        new IndexProgressRenderer(ctx), vectorPhase, settings.Reembed,
                        StaleLockWarningSink.ToStderr, embeddingsUnavailable));
            }
            else
            {
                renderer = settings.Events ? new IndexEventsRenderer(events!) : null;
                result = indexer.Index(
                    workspaceRoot, settings.Repo, settings.Full, settings.NoSemantic,
                    renderer, vectorPhase, settings.Reembed,
                    StaleLockWarningSink.ToStderr, embeddingsUnavailable);
            }

            events?.Completed(
                result, (int)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, renderer?.Symbols,
                provider: embeddings is null ? null : providerResolution?.Provider);

            if (settings.Json)
            {
                JsonOutput.Write(result);
            }
            else if (!settings.Events)
            {
                foreach (var repo in result.Repos)
                {
                    var ignoredPart = repo.Ignored > 0 ? $", ignored: {repo.Ignored}" : "";
                    AnsiConsole.MarkupLine(
                        $"[bold]{repo.Name}[/]: +{repo.Added} ~{repo.Modified} -{repo.Deleted} " +
                        $"(skipped: {repo.Skipped}{ignoredPart}, files: {repo.Files})");
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
