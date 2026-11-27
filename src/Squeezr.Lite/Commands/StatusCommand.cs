using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing.Query;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Lite.Config;
using Squeezr.Lite.Output;

namespace Squeezr.Lite.Commands;

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
            var status = new WorkspaceManager(workspaceRoot).GetStatus();
            var config = LiteConfigFile.TryLoad(workspaceRoot);
            var reconcile = ReconcilePendingNotice.Read(workspaceRoot);

            IReadOnlyDictionary<string, int> kindChunks;
            using (var store = new MetaStore(SqueezrPaths.MetaDbPath(workspaceRoot)))
            {
                kindChunks = store.CountChunksByKind();
            }

            if (settings.Json)
            {
                JsonOutput.Write(StatusJson.From(status, kindChunks, reconcile));
                foreach (var warning in config.Warnings)
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

            foreach (var line in ReconcilePendingNotice.Lines(reconcile))
            {
                AnsiConsole.MarkupLine($"[yellow]{line.EscapeMarkup()}[/]");
            }

            var age = SearchCommand.ResolveIndexAge(workspaceRoot);
            AnsiConsole.MarkupLine(age is { } indexAge
                ? $"[grey]Index age: {(int)indexAge.TotalMinutes} min — no background indexer runs in lite; " +
                  $"'{LiteRemedy.IndexCommand}' answers from the index as it was[/]"
                : $"[grey]Index age: unknown — '{LiteRemedy.IndexCommand}' has not run against this store yet[/]");

            if (config.Config is not null)
            {
                if (RankExclusion.Parse(config.Config.Indexing.RankExclude) is { IsActive: true, Patterns: var patterns })
                {
                    AnsiConsole.MarkupLine(
                        $"[grey]Rank exclude: {patterns.Count} pattern(s) — {string.Join(", ", patterns).EscapeMarkup()}[/]");
                }
            }

            AnsiConsole.MarkupLine("[grey]Semantic: unsupported — sqzrlite carries no embeddings, vector store or services; " +
                                   "search answers on the exact arm only[/]");
            foreach (var warning in config.Warnings)
            {
                AnsiConsole.MarkupLine($"[yellow]{warning.EscapeMarkup()}[/]");
            }

            return 0;
        }
        catch (WorkspaceException ex)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    public sealed record StatusJson(
        string Workspace,
        IReadOnlyList<RepoJson> Repos,
        IReadOnlyDictionary<string, int> KindChunks,
        WorktreeRecord? Reconcile,
        string Semantic)
    {
        public static StatusJson From(
            WorkspaceStatus status,
            IReadOnlyDictionary<string, int> kindChunks,
            WorktreeRecord? reconcile) => new(
            status.Name,
            [.. status.Repos.Select(r => new RepoJson(
                r.Name, r.Path, r.Files, r.Symbols, r.Chunks, r.IgnoredTotal, r.GuardWithheld, r.GuardDropped,
                r.LastIndexedAt))],
            kindChunks,
            reconcile,
            "unsupported");
    }

    public sealed record RepoJson(
        string Name,
        string Path,
        int Files,
        int Symbols,
        int Chunks,
        int Ignored,
        int GuardWithheld,
        int GuardDropped,
        DateTime? LastIndexedAt);
}
