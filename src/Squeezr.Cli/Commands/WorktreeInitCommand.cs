using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Embeddings;
using Squeezr.Cli.Output;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WorktreeInitCommand : Command<WorktreeInitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Worktree root to register; defaults to the current directory")]
        [CommandArgument(0, "[PATH]")]
        public string? Path { get; set; }

        [CommandOption("--name")]
        public string? Name { get; set; }

        [Description("Fill the worktree store from the main worktree. This is already the default: naming it " +
            "explicitly only makes the pair with --reindex deliberate. It fills a store this pass minted, or " +
            "one that still holds no rows")]
        [CommandOption("--copy")]
        public bool Copy { get; set; }

        [Description("Skip the copy and fill this worktree by indexing its own tree")]
        [CommandOption("--reindex")]
        public bool Reindex { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        try
        {
            if (settings.Copy && settings.Reindex)
            {
                return CommandError.Report(
                    new WorkspaceException(
                        "--copy and --reindex ask for opposite fills of the same store; pass one of them.",
                        WorkspaceException.InvalidQueryCode),
                    settings.Json);
            }

            var target = string.IsNullOrWhiteSpace(settings.Path)
                ? WorkspaceRootResolver.LaunchDirectory()
                : settings.Path!;

            var outcome = WorkspaceRootResolver.GuardRegistryIo(
                () => WorktreeRegistration.Register(target, settings.Name));

            // Guarded like the registration, because a registry read inside it that goes busy must still
            // read as registry contention. Store IO is not at risk of being repainted: every file failure
            // the copy path can raise is already a WorkspaceException naming the file it touched, and the
            // vector arm maps every Qdrant read it makes — the geometry read, both source counts, the
            // scroll, the writes and the count that confirms them — onto that shape rather than letting a
            // gRPC exception out of a command: whether a stop is reported or refused is decided by whether
            // this pass was already asked to touch the lane's collection — before that it is reported with
            // its own reason, from that moment it is a refusal carrying the numbers it stopped on. It
            // blocks here because the verb is synchronous: a page scroll against a
            // live Qdrant is the longest wait this command takes, and it runs after both store locks are
            // down — never holding the parent for a round trip the parent's own data does not need.
            var fill = WorkspaceRootResolver.GuardRegistryIo(() => WorktreeStoreFill.Fill(
                outcome,
                copy: !settings.Reindex,
                () => EmbeddingRuntimeFactory.ResolveModelKey(outcome.RootPath),
                StaleLockWarningSink.ToStderr,
                request => WorktreeVectorCopier.CopyAsync(request, cancellationToken)
                    .GetAwaiter().GetResult()));

            if (outcome is { State: WorktreeRegistrationState.Registered, WorkspaceName: { } registeredName })
            {
                RegistrationAdvisoryOutput.Write(
                    RegistrationAdvisories.Collect(registeredName, outcome.RootPath),
                    settings.Json);
            }

            var report = WorktreeInitReport.From(outcome, fill);
            if (report.RepairHint is not null)
            {
                Console.Error.WriteLine($"warning: {report.RepairHint}");
            }

            // Rows the copied database holds and this worktree's manifest could not be made to answer for
            // are the state where a store has bytes and no answer: they belong on the same channel the
            // registry hint uses, not buried in a fill block a --json reader has to walk.
            foreach (var unowned in report.Fill?.Copied?.UnownedRepos ?? [])
            {
                Console.Error.WriteLine($"warning: {unowned}");
            }

            // An arm that ran and moved nothing is the same 'the store has bytes and no answer' state one
            // layer down: the copied database already calls these chunks embedded, and only the collection
            // can say whether they are. A session-start reader has to see that on the channel warnings
            // travel on, not only inside a fill block.
            if (report.Fill?.Vectors is { Copied: false, Reason: { } notCopied })
            {
                Console.Error.WriteLine($"warning: {notCopied}");
            }

            if (settings.Json)
            {
                JsonOutput.Write(report);
                return 0;
            }

            WriteHuman(report, outcome.State);
            return 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or IOException or UnauthorizedAccessException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static void WriteHuman(WorktreeInitReport report, WorktreeRegistrationState state)
    {
        if (state is WorktreeRegistrationState.ParentNotIndexed or WorktreeRegistrationState.NoParentRootToName)
        {
            AnsiConsole.MarkupLine($"[yellow]{report.Reason!.EscapeMarkup()}[/]");
            return;
        }

        AnsiConsole.MarkupLine(state == WorktreeRegistrationState.Registered
            ? $"Initialized worktree store [bold]{report.StoreKey!.EscapeMarkup()}[/] in {report.RootPath.EscapeMarkup()}"
            : $"Worktree store [bold]{report.StoreKey!.EscapeMarkup()}[/] is already registered in " +
              $"{report.RootPath.EscapeMarkup()} — nothing created");

        if (report.Reason is not null)
        {
            AnsiConsole.MarkupLine($"  [grey]{report.Reason.EscapeMarkup()}[/]");
        }

        AnsiConsole.MarkupLine($"  branch: {BranchText(report).EscapeMarkup()}");
        AnsiConsole.MarkupLine(
            $"  parent: {(report.ParentRootPath ?? "unknown").EscapeMarkup()}");
        AnsiConsole.MarkupLine($"  collection: {report.Collection!.EscapeMarkup()}");
        AnsiConsole.MarkupLine($"  serve: {report.ServeCommand!.EscapeMarkup()}");
        WriteFill(report.Fill);
        AnsiConsole.MarkupLine(IndexLine(report));
    }

    private static void WriteFill(WorktreeFillReport? fill)
    {
        if (fill is null)
        {
            return;
        }

        if (fill.Copied is not { } copy)
        {
            AnsiConsole.MarkupLine($"  fill: [grey]{(fill.Reason ?? "nothing copied").EscapeMarkup()}[/]");
            return;
        }

        AnsiConsole.MarkupLine("  fill: [grey]three numbers per file — source → staged copy → installed[/]");
        var database = copy.Database;
        AnsiConsole.MarkupLine(
            $"  fill: meta.db {database.SourceBytes} → {database.CopiedBytes} → {database.TargetBytes} bytes " +
            $"({HumanSize(database.TargetBytes)} installed) — " +
            $"files {database.SourceFiles}→{database.CopiedFiles}→{database.TargetFiles}, " +
            $"chunks {database.SourceChunks}→{database.CopiedChunks}→{database.TargetChunks}");
        var trigram = copy.Trigram;
        AnsiConsole.MarkupLine(trigram.Copied
            ? $"  fill: trigram.idx {trigram.SourceBytes} → {trigram.CopiedBytes} → {trigram.TargetBytes} bytes " +
              $"({HumanSize(trigram.TargetBytes)} installed) — docs {trigram.SourceDocs}→{trigram.CopiedDocs}→{trigram.TargetDocs}"
            : "  fill: trigram.idx [grey]absent in the main worktree store[/]");
        AnsiConsole.MarkupLine(copy.OwnedRepos.Count > 0
            ? $"  fill: manifest owns {string.Join(", ", copy.OwnedRepos.Select(name => name.EscapeMarkup()))}"
            : "  fill: manifest already owned every copied row");
        // The semantic arm gets its own triple in points rather than bytes, and the state where no vector
        // store was asked is spelled out: a lane whose parent holds pending rows only inherits an empty
        // collection, and a reader who is not told that will go looking for vectors that were never there.
        AnsiConsole.MarkupLine(WorktreeVectorsFillLine.Build(fill.Vectors));
    }

    private static string IndexLine(WorktreeInitReport report) =>
        report.Fill?.Copied is not null
            ? "  index: copied from the main worktree — [yellow]reconcile pending[/], run [bold]squeezr index[/] " +
              "here so this branch answers with its own coordinates"
            : report.ReconcilePending switch
            {
                true => "  index: [yellow]empty[/] — nothing has been copied or reconciled into this store yet",
                false => "  index: has content — [bold]squeezr status[/] breaks it down",
                _ => "  index: reconcile state unknown until the store reports it",
            };

    // The triple is printed in raw bytes and the human size names only the installed file: a copy that
    // moved nothing has to be visible as zero bytes, not rounded away into 0.0 KB.
    private static string HumanSize(long bytes) =>
        bytes >= 1L << 20
            ? $"{bytes / 1048576.0:F1} MB"
            : bytes >= 1024
                ? $"{bytes / 1024.0:F1} KB"
                : $"{bytes} B";

    private static string BranchText(WorktreeInitReport report) =>
        report.Branch ?? (report.IsDetached ? "detached head" : "unknown");
}
