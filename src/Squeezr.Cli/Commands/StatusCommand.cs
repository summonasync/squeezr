using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class StatusCommand : Command<StatusCommand.Settings>
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
            var workspaceRoot = WorkspaceRootResolver.RequireWorkspaceRoot();
            var manager = new WorkspaceManager(workspaceRoot);
            var status = manager.GetStatus();
            var config = LoadConfigIfReadable(workspaceRoot);

            var warnings = EmbeddingRuntimeFactory.TryCreate(
                workspaceRoot, withProvider: false, out _, out var vectorStore);
            foreach (var warning in warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }

            QdrantStatus qdrant;
            using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot)))
            {
                qdrant = QdrantStatusProbe.ProbeAsync(vectorStore, store, cancellationToken).GetAwaiter().GetResult();
            }

            status = status with { Qdrant = qdrant };

            if (settings.Json)
            {
                var staleFiles = StaleFileDetector.Detect(workspaceRoot);
                var report = WorkspaceStatusReport.Create(status, staleFiles, config?.Embeddings.Provider);
                JsonOutput.Write(report);
                foreach (var warning in report.Warnings)
                {
                    Console.Error.WriteLine($"warning: {warning}");
                }
                return 0;
            }

            AnsiConsole.MarkupLine(
                $"Workspace [bold]{status.Name.EscapeMarkup()}[/] — {status.Repos.Count} repo(s), " +
                $"{status.Repos.Sum(r => r.Files)} file(s) indexed");
            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("Repo");
            table.AddColumn("Path");
            table.AddColumn("Files");
            table.AddColumn("Symbols");
            table.AddColumn("Chunks");
            table.AddColumn("Ignored");
            table.AddColumn("Withheld");
            table.AddColumn("Dropped");
            table.AddColumn("Last indexed");

            foreach (var repo in status.Repos)
            {
                table.AddRow(
                    repo.Name.EscapeMarkup(),
                    repo.Path.EscapeMarkup(),
                    repo.Files.ToString(),
                    repo.Symbols.ToString(),
                    repo.Chunks.ToString(),
                    repo.IgnoredTotal > 0 ? repo.IgnoredTotal.ToString() : "-",
                    repo.GuardWithheld.ToString(),
                    repo.GuardDropped.ToString(),
                    repo.LastIndexedAt?.ToString("yyyy-MM-dd HH:mm") ?? "-");
            }

            AnsiConsole.Write(table);

            if (RepoGuardReport.FromTally(status.Repos) is { } guard)
            {
                AnsiConsole.MarkupLine($"[grey]{guard.Warning().EscapeMarkup()}[/]");
            }

            if (config is not null)
            {
                AnsiConsole.MarkupLine($"Embeddings provider: {config.Embeddings.Provider.EscapeMarkup()}");
            }

            if (qdrant.Available)
            {
                AnsiConsole.MarkupLine(
                    $"Qdrant: [green]available[/] — {qdrant.Points} point(s), {qdrant.PendingChunks} pending chunk(s){CoverageSuffix(qdrant)}");
            }
            else
            {
                AnsiConsole.MarkupLine($"Qdrant: [red]unavailable[/] — {qdrant.PendingChunks} pending chunk(s){CoverageSuffix(qdrant)}");
                var hint = config is null
                    ? null
                    : QdrantEndpointHint.RestPortMisconfiguration(config.Qdrant.Endpoint);
                if (hint is not null)
                {
                    AnsiConsole.MarkupLine($"[grey]{hint.EscapeMarkup()}[/]");
                }
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static string CoverageSuffix(QdrantStatus qdrant) =>
        new SemanticCoverage(qdrant.TotalChunks, qdrant.PendingChunks).Ratio is { } ratio
            ? $", semantic coverage {(int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero)}%"
            : string.Empty;

    private static SqueezrConfig? LoadConfigIfReadable(string workspaceRoot)
    {
        if (!File.Exists(SqueezrPaths.ConfigPath(workspaceRoot)))
        {
            return null;
        }

        try
        {
            return ConfigFile.Load(workspaceRoot);
        }
        catch (WorkspaceException)
        {
            return null;
        }
    }
}
