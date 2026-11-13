using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Indexing.Query;
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
            var config = LoadConfigIfReadable(workspaceRoot, out var configError);
            var onnxStore = configError?.Code == WorkspaceException.OnnxProviderCode;
            var siblings = WorkspaceSiblingSnapshot.Build(workspaceRoot);

            var warnings = EmbeddingRuntimeFactory.TryCreate(
                workspaceRoot, withProvider: false, out _, out var vectorStore);
            if (!onnxStore)
            {
                foreach (var warning in warnings)
                {
                    Console.Error.WriteLine($"warning: {warning}");
                }
            }

            QdrantStatus qdrant;
            IReadOnlyDictionary<string, int> kindChunks;
            using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot)))
            {
                qdrant = QdrantStatusProbe.ProbeAsync(vectorStore, store, cancellationToken).GetAwaiter().GetResult();
                kindChunks = store.CountChunksByKind();
            }

            status = status with { Qdrant = qdrant };

            if (settings.Json)
            {
                var staleFiles = DetectStaleFilesIfReadable(workspaceRoot);
                var report = WorkspaceStatusReport.Create(
                    status, staleFiles, config?.Embeddings.Provider, siblings,
                    FleetWatchAdoption.FromStore(workspaceRoot),
                    ReconcilePendingNotice.Read(workspaceRoot)) with
                { KindChunks = kindChunks };
                if (onnxStore)
                {
                    report = report with { Warnings = [.. report.Warnings, .. OnnxStoreDiagnostic.Lines()] };
                }
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

            if (kindChunks.Count > 0)
            {
                var byKind = string.Join(", ", kindChunks.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"));
                AnsiConsole.MarkupLine($"[grey]Chunks by kind: {byKind.EscapeMarkup()}[/]");
            }

            if (RepoGuardReport.FromTally(status.Repos) is { } guard)
            {
                AnsiConsole.MarkupLine($"[grey]{guard.Warning().EscapeMarkup()}[/]");
            }

            // The store's own reconcile record, on the same surface that prints its file counts: a lane that
            // answers with the main worktree's coordinates has to say so where a reader looks at the numbers,
            // not only on the stderr channel a machine reads.
            foreach (var line in ReconcilePendingNotice.Lines(ReconcilePendingNotice.Read(workspaceRoot)))
            {
                AnsiConsole.MarkupLine($"[yellow]{line.EscapeMarkup()}[/]");
            }

            RenderWorkspaces(siblings);

            if (config is not null)
            {
                AnsiConsole.MarkupLine($"Embeddings provider: {config.Embeddings.Provider.EscapeMarkup()}");
                if (RankExclusion.Parse(config.Indexing.RankExclude) is { IsActive: true, Patterns: var patterns })
                {
                    AnsiConsole.MarkupLine(
                        $"[grey]Rank exclude: {patterns.Count} pattern(s) — {string.Join(", ", patterns).EscapeMarkup()}[/]");
                }
            }

            if (onnxStore)
            {
                foreach (var line in OnnxStoreDiagnostic.Lines())
                {
                    AnsiConsole.MarkupLine($"[yellow]{line.EscapeMarkup()}[/]");
                }
            }

            if (FleetWatchAdoption.FromStore(workspaceRoot) is { } adoption)
            {
                AnsiConsole.MarkupLine(
                    $"[grey]Watch claim last adopted by pid {adoption.AdopterPid} ({adoption.AdopterHost.EscapeMarkup()}) " +
                    $"at {adoption.AdoptedAt.ToLocalTime():yyyy-MM-dd HH:mm}, taking over from dead pid {adoption.DisplacedPid}[/]");
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

    private static void RenderWorkspaces(WorkspaceSiblingSnapshot siblings)
    {
        if (siblings.Workspaces.Count == 0)
        {
            return;
        }

        var table = new Table { Border = TableBorder.Rounded, Title = new TableTitle("Workspaces") };
        table.AddColumn("Name");
        table.AddColumn("Root path");
        table.AddColumn("Store key");
        table.AddColumn("Current");
        table.AddColumn("Stale");
        foreach (var entry in siblings.Workspaces)
        {
            table.AddRow(
                entry.Name.EscapeMarkup(),
                entry.RootPath.EscapeMarkup(),
                entry.StoreKey.EscapeMarkup(),
                entry.Current ? "[green]current[/]" : "-",
                entry.Stale ? "[yellow]stale[/]" : "-");
        }

        AnsiConsole.Write(table);
        if (siblings.Omitted > 0)
        {
            AnsiConsole.MarkupLine($"[grey]{siblings.Omitted} more workspace(s) registered — not listed.[/]");
        }
    }

    private static IReadOnlyList<string> DetectStaleFilesIfReadable(string workspaceRoot)
    {
        try
        {
            return StaleFileDetector.Detect(workspaceRoot);
        }
        catch (WorkspaceException ex) when (ex.Code == WorkspaceException.OnnxProviderCode)
        {
            return [];
        }
    }

    private static SqueezrConfig? LoadConfigIfReadable(string workspaceRoot, out WorkspaceException? configError)
    {
        configError = null;
        if (!File.Exists(SqueezrPaths.ConfigPath(workspaceRoot)))
        {
            return null;
        }

        try
        {
            return ConfigFile.Load(workspaceRoot);
        }
        catch (WorkspaceException ex)
        {
            configError = ex;
            return null;
        }
    }
}
