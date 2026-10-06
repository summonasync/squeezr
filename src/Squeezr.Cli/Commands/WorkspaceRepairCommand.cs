using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WorkspaceRepairCommand : AsyncCommand<WorkspaceRepairCommand.Settings>
{
    private const string HuskDropHint =
        "run 'squeezr drop --store-key <key>' to remove this entry and its store";

    private const string DropEquivalent = "squeezr drop --store-key";

    public sealed class Settings : CommandSettings
    {
        [CommandOption("--store-key <STORE_KEY>")]
        public string? StoreKey { get; set; }

        [CommandOption("--root <PATH>")]
        public string? Root { get; set; }

        [Description("List what repair would adopt and prune, with store sizes; changes nothing")]
        [CommandOption("--dry-run")]
        public bool DryRun { get; set; }

        [Description(
            "Prune every stale row without prompting. Back up the registry first: the tool rotates workspaces.json.bak " +
            "on each write, so it keeps only the state one removal back, not the state before the sweep")]
        [CommandOption("--yes")]
        public bool Yes { get; set; }

        [CommandOption("--json")]
        public bool Json { get; set; }
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context,
        Settings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            if ((settings.StoreKey is null) != (settings.Root is null))
            {
                throw new McpUsageException("--store-key and --root must be provided together.");
            }

            if (settings.DryRun && settings.Yes)
            {
                throw new McpUsageException("Options --dry-run and --yes are mutually exclusive.");
            }

            if (settings.StoreKey is not null && (settings.DryRun || settings.Yes))
            {
                throw new McpUsageException("--dry-run and --yes sweep the whole registry; run them without --store-key.");
            }

            if (settings.StoreKey is not null)
            {
                var entry = WorkspaceRootResolver.GuardRegistryIo(
                    () => RegistryRepair.AdoptWithRoot(settings.StoreKey, settings.Root!));
                var adopted = new WorkspaceRepairAdoptedEntry(entry.Name, entry.StoreKey, entry.RootPath);
                if (settings.Json)
                {
                    JsonOutput.Write(adopted);
                }
                else
                {
                    AnsiConsole.MarkupLine(
                        $"Adopted workspace [bold]{entry.Name.EscapeMarkup()}[/] " +
                        $"({entry.StoreKey.EscapeMarkup()}) with root {entry.RootPath.EscapeMarkup()}");
                }

                return 0;
            }

            var outcome = settings.DryRun
                ? Preview()
                : WorkspaceRootResolver.GuardRegistryIo(RegistryRepair.AdoptAll);

            var adoptedRows = outcome.Adopted
                .Select(orphan => new WorkspaceRepairAdoptedEntry(orphan.Name, orphan.StoreKey, orphan.RootPath!))
                .ToList();
            var needsRootRows = outcome.NeedsRoot
                .Select(orphan => new WorkspaceRepairNeedsRootEntry(orphan.Name, orphan.StoreKey))
                .ToList();
            var huskRows = outcome.Husks
                .Select(husk => new WorkspaceRepairHuskEntry(husk.Name, husk.StoreKey, husk.RootPath))
                .ToList();
            var candidates = WorkspaceRepairPruner.Candidates(
                outcome.Stale,
                huskRows.Select(husk => husk.StoreKey).ToHashSet(StringComparer.Ordinal));

            WriteHuskHints(huskRows);

            if (!settings.Json)
            {
                RenderReport(adoptedRows, needsRootRows, huskRows, candidates, settings.DryRun);
            }

            var gate = WorkspaceRepairPruner.Gate(
                candidates.Count,
                settings.DryRun,
                settings.Yes,
                AnsiConsole.Profile.Capabilities.Interactive && !settings.Json);

            var prune = await WorkspaceRepairPruner.RunAsync(
                gate, candidates, AnsiConsole.Console, cancellationToken);

            if (settings.Json)
            {
                JsonOutput.Write(new WorkspaceRepairReport(adoptedRows, needsRootRows, huskRows, candidates, prune));
            }
            else
            {
                RenderPrune(prune, candidates);
            }

            if (prune.Mode == WorkspaceRepairPruner.ModeAborted)
            {
                return CommandError.Report(new WorkspaceException(SweepFailure(prune)), settings.Json);
            }

            return gate == RepairPruneGate.Refuse
                ? CommandError.Report(new McpUsageException(PruneRefusal(candidates.Count, settings.Json)), settings.Json)
                : 0;
        }
        catch (Exception ex) when (ex is WorkspaceException or McpUsageException)
        {
            return CommandError.Report(ex, settings.Json);
        }
    }

    private static RepairOutcome Preview()
    {
        var scan = WorkspaceRootResolver.GuardRegistryIo(OrphanScanner.Scan);
        return new RepairOutcome(
            [.. scan.Orphans.Where(orphan => orphan.IsAdoptable)],
            [.. scan.Orphans.Where(orphan => !orphan.IsAdoptable)],
            scan.Husks,
            scan.Stale);
    }

    private static void RenderReport(
        IReadOnlyList<WorkspaceRepairAdoptedEntry> adopted,
        IReadOnlyList<WorkspaceRepairNeedsRootEntry> needsRoot,
        IReadOnlyList<WorkspaceRepairHuskEntry> husks,
        IReadOnlyList<WorkspaceRepairStaleEntry> stale,
        bool preview)
    {
        if (adopted.Count == 0 && needsRoot.Count == 0 && husks.Count == 0 && stale.Count == 0)
        {
            AnsiConsole.MarkupLine("Nothing to repair: no orphaned workspace stores found.");
            return;
        }

        if (preview)
        {
            AnsiConsole.MarkupLine("[grey]Dry run: nothing adopted, nothing deleted.[/]");
        }

        AnsiConsole.MarkupLine(
            $"Repair report: [green]{adopted.Count} {(preview ? "adoptable" : "adopted")}[/], " +
            $"[yellow]{needsRoot.Count} need --root[/], " +
            $"{husks.Count} husk(s) left untouched, " +
            $"{stale.Count} stale row(s)");

        foreach (var entry in adopted)
        {
            AnsiConsole.MarkupLine(
                $"  [green]+[/] {(preview ? "would adopt" : "adopted")} {entry.Name.EscapeMarkup()} " +
                $"({entry.StoreKey.EscapeMarkup()}) → {entry.RootPath.EscapeMarkup()}");
        }

        foreach (var orphan in needsRoot)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]![/] needs root: {orphan.Name.EscapeMarkup()} ({orphan.StoreKey.EscapeMarkup()}) — " +
                $"re-run with --store-key {orphan.StoreKey.EscapeMarkup()} --root <path>");
        }

        foreach (var row in stale)
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]-[/] stale: {row.Name.EscapeMarkup()} ({row.StoreKey.EscapeMarkup()}) " +
                $"root {row.RootPath.EscapeMarkup()} · {StoreSizeLabel(row)}");
        }
    }

    private static void RenderPrune(WorkspaceRepairPruneSummary prune, IReadOnlyList<WorkspaceRepairStaleEntry> candidates)
    {
        switch (prune.Mode)
        {
            case WorkspaceRepairPruner.ModeNone:
                return;
            case WorkspaceRepairPruner.ModeDryRun:
                AnsiConsole.MarkupLine(
                    $"Dry run: {candidates.Count} stale row(s), " +
                    $"{DryRunStoresText(candidates, prune.TotalStoreBytes)}. Nothing was deleted.");
                AnsiConsole.MarkupLine($"[grey]{BackupAdvice().EscapeMarkup()}[/]");
                return;
            case WorkspaceRepairPruner.ModeRefused:
                AnsiConsole.MarkupLine($"[yellow]Not pruned:[/] {candidates.Count} stale row(s) left in the registry.");
                AnsiConsole.MarkupLine($"[grey]{BackupAdvice().EscapeMarkup()}[/]");
                return;
            case WorkspaceRepairPruner.ModeDeclined:
                AnsiConsole.MarkupLine($"[yellow]Prune declined:[/] {candidates.Count} stale row(s) left in the registry.");
                return;
        }

        foreach (var row in prune.Rows)
        {
            AnsiConsole.MarkupLine(PruneLine(row));
        }

        var pruned = prune.Rows.Count(row =>
            row.Action is WorkspaceRepairPruner.ActionRowAndStore or WorkspaceRepairPruner.ActionRowOnly);
        var stores = prune.StoresRemoved;
        var held = prune.Rows.Count(row => row.Action == WorkspaceRepairPruner.ActionSkippedUnsafeKey);

        if (prune.Mode == WorkspaceRepairPruner.ModeAborted)
        {
            var notAttempted = candidates.Count - prune.Rows.Count;
            AnsiConsole.MarkupLine(
                $"[red]Sweep stopped:[/] {SweepFailure(prune).EscapeMarkup()} — {pruned} stale row(s) already removed, " +
                $"{stores} store dir(s) gone" +
                (notAttempted > 0 ? $", {notAttempted} row(s) not attempted" : string.Empty) + ".");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[green]Pruned {pruned} stale row(s)[/] · {RemovedStoresText(stores, prune.TotalStoreBytes)}" +
                (held > 0 ? $" · [yellow]{held} left alone[/]" : string.Empty) + ".");
        }

        if (pruned > 0)
        {
            AnsiConsole.MarkupLine(
                $"[grey]Registry {WorkspaceRegistry.FilePath.EscapeMarkup()}: workspaces.json.bak now holds the state " +
                "one removal back, not the state before this sweep.[/]");
        }
    }

    private static string PruneLine(WorkspaceRepairPrunedEntry row)
    {
        var named = $"{row.Name.EscapeMarkup()} ({row.StoreKey.EscapeMarkup()})";
        return row.Action switch
        {
            WorkspaceRepairPruner.ActionRowAndStore =>
                $"  [green]-[/] pruned row + store{SizeParenthesis(row.StoreBytes)}: {named}" +
                $" — performed: {DropEquivalent} {row.StoreKey.EscapeMarkup()}",
            WorkspaceRepairPruner.ActionRowOnly =>
                $"  [yellow]-[/] pruned registry row only (no store dir): {named}" +
                $" — performed: {DropEquivalent} {row.StoreKey.EscapeMarkup()}",
            WorkspaceRepairPruner.ActionPruneFailed =>
                $"  [red]x[/] not removed: {named} — {FailureText(row.Failure)}",
            WorkspaceRepairPruner.ActionSkippedUnsafeKey =>
                $"  [red]![/] left alone, store key is not a single path segment: {named}",
            _ => $"  [red]![/] left alone ({row.Action.EscapeMarkup()}): {named}",
        };
    }

    private static string SizeParenthesis(long? bytes) =>
        bytes is { } value ? $" ({StoreSizeText.Format(value)})" : string.Empty;

    private static string StoreSizeLabel(WorkspaceRepairStaleEntry row) =>
        row.StoreState switch
        {
            WorkspaceRepairPruner.StoreMeasured when row.StoreBytes is { } value => StoreSizeText.Format(value),
            WorkspaceRepairPruner.StoreMissing => "no store dir",
            WorkspaceRepairPruner.StoreUnreadable => "size unknown, store dir unreadable",
            WorkspaceRepairPruner.StoreNotMeasured => "size not measured, store key is not a single path segment",
            _ => "size not measured",
        };

    // Aggregate wording follows the removal fact, never the byte sum or the row shape: a store dir can be
    // empty, or it can exist behind a refused key and simply not have been touched by this sweep.
    private static string RemovedStoresText(int stores, long bytes) =>
        stores > 0 ? $"{stores} store dir(s) removed ({StoreSizeText.Format(bytes)})" : "no store dirs removed";

    private static string DryRunStoresText(IReadOnlyList<WorkspaceRepairStaleEntry> candidates, long bytes)
    {
        var present = candidates
            .Where(row => row.StoreState is WorkspaceRepairPruner.StoreMeasured or WorkspaceRepairPruner.StoreUnreadable)
            .ToList();
        if (present.Count == 0)
        {
            return candidates.Any(row => row.StoreState == WorkspaceRepairPruner.StoreNotMeasured)
                ? "store dirs could not be checked for every row"
                : "registry rows only — no store dirs to remove";
        }

        var total = present.Any(row => row.StoreBytes is null)
            ? $"at least {StoreSizeText.Format(bytes)}"
            : StoreSizeText.Format(bytes);
        return $"{present.Count} store dir(s) totalling {total} would be removed";
    }

    private static string SweepFailure(WorkspaceRepairPruneSummary prune) =>
        FailureText(prune.Rows.FirstOrDefault(row => row.Failure is not null)?.Failure);

    private static string FailureText(string? failure) =>
        (failure ?? "a store dir could not be removed").EscapeMarkup();

    // A dated copy, not the rotating .bak: the sweep calls Save once per row, so the backup the
    // tool keeps holds only the state one removal back. Wording stays paste-safe in --json output.
    private static string BackupAdvice() =>
        $"before a real prune take a dated copy of the registry: cp {WorkspaceRegistry.FilePath} " +
        $"{WorkspaceRegistry.FilePath}.bak-{DateTimeOffset.UtcNow:yyyy-MM-dd}";

    private static string PruneRefusal(int count, bool json) =>
        $"Refusing to prune {count} stale row(s) without confirmation: " +
        (json
            ? "--json writes one machine-readable document to stdout, so a prompt has no channel here"
            : "this is not an interactive terminal") +
        $", and the absence of a prompt is not consent. Re-run with --dry-run to list them, or --yes to confirm; " +
        $"{BackupAdvice()}.";

    private static void WriteHuskHints(IReadOnlyList<WorkspaceRepairHuskEntry> husks)
    {
        foreach (var husk in husks)
        {
            Console.Error.WriteLine(
                $"warning: workspace '{husk.Name}' looks like a husk (root path '{husk.RootPath}'); {HuskDropHint}.");
        }
    }
}
