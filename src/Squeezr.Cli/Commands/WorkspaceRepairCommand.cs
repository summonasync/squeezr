using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class WorkspaceRepairCommand : AsyncCommand<WorkspaceRepairCommand.Settings>
{
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
            "Prune every stale or husk row without prompting. Back up the registry first: the tool rotates " +
            "workspaces.json.bak on each write, so it keeps only the state one removal back, not the state " +
            "before the sweep")]
        [CommandOption("--yes")]
        public bool Yes { get; set; }

        [Description(
            "Also delete the Qdrant collections no surviving registry row names. This is the destructive door " +
            "for the collection arm only: without it the sweep reports and touches nothing, and a row that " +
            "names a collection still protects it")]
        [CommandOption("--apply")]
        public bool Apply { get; set; }

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
            SqueezrHome.RequireHabitableProfileHome();
            if ((settings.StoreKey is null) != (settings.Root is null))
            {
                throw new McpUsageException("--store-key and --root must be provided together.");
            }

            if (settings.DryRun && settings.Yes)
            {
                throw new McpUsageException("Options --dry-run and --yes are mutually exclusive.");
            }

            if (settings.DryRun && settings.Apply)
            {
                throw new McpUsageException("Options --dry-run and --apply are mutually exclusive.");
            }

            if (settings.StoreKey is not null && (settings.DryRun || settings.Yes || settings.Apply))
            {
                throw new McpUsageException(
                    "--dry-run, --yes and --apply sweep the whole registry; run them without --store-key.");
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
                .Select(orphan => new WorkspaceRepairNeedsRootEntry(
                    orphan.Name,
                    orphan.StoreKey,
                    orphan.RootPath,
                    orphan.RootPath is null ? "missing-root" : "root-gone"))
                .ToList();
            var failedRows = outcome.Failed
                .Select(failure => new WorkspaceRepairFailedAdoptionEntry(
                    failure.Orphan.Name,
                    failure.Orphan.StoreKey,
                    failure.Cause))
                .ToList();

            // Husk and stale sets are disjoint by construction (a husk root exists, a stale root does not);
            // both candidate builders still exclude each other's keys so a registry that breaks the
            // invariant can never route one row into both verdicts.
            var staleKeys = outcome.Stale.Select(row => row.StoreKey).ToHashSet(StringComparer.Ordinal);
            var huskRows = WorkspaceRepairPruner.HuskCandidates(outcome.Husks, staleKeys);
            var candidates = WorkspaceRepairPruner
                .Candidates(outcome.Stale, huskRows.Select(husk => husk.StoreKey).ToHashSet(StringComparer.Ordinal))
                .Concat(huskRows)
                .OrderBy(row => row.Name, StringComparer.Ordinal)
                .ThenBy(row => row.StoreKey, StringComparer.Ordinal)
                .ToList();

            if (!settings.Json)
            {
                RenderReport(adoptedRows, needsRootRows, failedRows, candidates, settings.DryRun);
            }

            var gate = WorkspaceRepairPruner.Gate(
                candidates.Count,
                settings.DryRun,
                settings.Yes,
                AnsiConsole.Profile.Capabilities.Interactive && !settings.Json);

            var prune = await WorkspaceRepairPruner.RunAsync(
                gate, candidates, AnsiConsole.Console, cancellationToken);

            WriteHuskHints(huskRows, prune);

            // Rows before collections, and the sweep reads the registry itself rather than a snapshot handed
            // to it: what it can protect is what survived the row sweep above. A collection no surviving row
            // names is a leak from a store that is already gone; a collection a live row names is that row's
            // own data, whatever the tree above just did with the other rows.
            var collections = await SurveyCollections(settings, prune, cancellationToken);
            WriteCollectionHints(collections);

            if (settings.Json)
            {
                JsonOutput.Write(new WorkspaceRepairReport(
                    adoptedRows,
                    needsRootRows,
                    HuskSection(huskRows),
                    [.. candidates.Where(row => row.Category == WorkspaceRepairPruner.CategoryStale)],
                    prune,
                    Counts(adoptedRows.Count, needsRootRows.Count, candidates, prune, settings.DryRun),
                    failedRows.Count > 0 ? failedRows : null,
                    collections));
            }
            else
            {
                RenderPrune(prune, candidates);
                RenderCollections(collections);
                RenderSummary(adoptedRows.Count, needsRootRows.Count, candidates, prune, settings.DryRun);
            }

            // The adopt phase runs before the sweep, so its verdict is the first phase fact a broken
            // home produces; the sweep rows stay in the report either way.
            if (failedRows.Count > 0)
            {
                return CommandError.Report(new WorkspaceException(AdoptionFailure(failedRows)), settings.Json);
            }

            if (prune.Mode == WorkspaceRepairPruner.ModeAborted)
            {
                return CommandError.Report(new WorkspaceException(SweepFailure(prune)), settings.Json);
            }

            return gate == RepairPruneGate.Refuse
                ? CommandError.Report(
                    new McpUsageException(PruneRefusal(WorkspaceRepairPruner.CandidateLabel(candidates), settings.Json)),
                    settings.Json)
                : 0;
        }
        catch (Exception ex)
            when (ex is WorkspaceException or McpUsageException or UnauthorizedAccessException or ArgumentException)
        {
            // Same permission net as drop: a denial no phase classified would otherwise leave the CLI
            // with exit 255 and no parseable output on any channel.
            var reported = ex is UnauthorizedAccessException
                ? new WorkspaceException(ex.Message, WorkspaceException.DefaultCode, ex)
                : ex;
            return CommandError.Report(reported, settings.Json);
        }
    }

    private static RepairOutcome Preview()
    {
        var scan = WorkspaceRootResolver.GuardRegistryIo(OrphanScanner.Scan);
        return new RepairOutcome(
            [.. scan.Orphans.Where(orphan => orphan.IsAdoptable)],
            [.. scan.Orphans.Where(orphan => !orphan.IsAdoptable)],
            scan.Husks,
            scan.Stale,
            []);
    }

    // The destructive half of the collection arm unlocks only on a run whose row phase reached a confirmed
    // sweep: a run that refused to prune, declined the prompt or died mid-sweep has not earned a delete on
    // any endpoint, and a dry run previews. Reporting itself is unconditional — a leak nobody listed reads
    // as a clean registry.
    private static async Task<WorkspaceRepairCollectionsReport> SurveyCollections(
        Settings settings,
        WorkspaceRepairPruneSummary prune,
        CancellationToken cancellationToken)
    {
        var rows = WorkspaceRootResolver.GuardRegistryIo(WorkspaceRegistry.Load);
        var confirmed = prune.Mode
            is WorkspaceRepairPruner.ModePruned
            or WorkspaceRepairPruner.ModeNone;
        return await WorkspaceCollectionSweep.RunAsync(
            WorkspaceCollectionSweep.EndpointsOf(rows),
            settings.Apply && confirmed,
            cancellationToken);
    }

    // An endpoint that never answered is a hole in the ledger, and a --json reader has to see it on the
    // channel warnings travel on: silence here would read as "the sweep looked everywhere and found nothing".
    private static void WriteCollectionHints(WorkspaceRepairCollectionsReport collections)
    {
        foreach (var endpoint in collections.UnreachableEndpoints)
        {
            Console.Error.WriteLine(
                $"warning: the collection sweep could not reach '{endpoint}'; collections living there are not in " +
                "this ledger, and nothing on it was deleted.");
        }
    }

    private static void RenderCollections(WorkspaceRepairCollectionsReport collections)
    {
        if (collections.SkipReason is { } skipped)
        {
            AnsiConsole.MarkupLine($"[grey]Collections: survey skipped — {skipped.EscapeMarkup()}[/]");
            return;
        }

        foreach (var entry in collections.Entries)
        {
            var named = $"{entry.Collection.EscapeMarkup()} on {entry.Endpoint.EscapeMarkup()}";
            AnsiConsole.MarkupLine(entry.Action switch
            {
                WorkspaceCollectionSweep.ActionReaped => $"  [green]-[/] collection {named} — reaped",
                WorkspaceCollectionSweep.ActionReapFailed =>
                    $"  [red]x[/] collection {named} — not reaped: " +
                    $"{(entry.Failure ?? "no reason given").EscapeMarkup()}",
                _ => $"  [yellow]-[/] collection {named} — would be reaped (pass --apply)",
            });
        }

        var reaped = collections.Entries.Count(entry => entry.Action == WorkspaceCollectionSweep.ActionReaped);
        AnsiConsole.MarkupLine(
            $"[grey]Collections: {collections.Entries.Count} unreferenced of {collections.SeenMinted} " +
            $"collection(s) this product minted on {collections.Endpoints.Count} endpoint(s) — " +
            $"{(collections.Apply
                ? $"{reaped} reaped"
                : "reported only, nothing deleted (pass --apply)")}[/]" +
            (collections.UnreachableEndpoints.Count > 0
                ? $"[grey], {collections.UnreachableEndpoints.Count} endpoint(s) unreachable[/]"
                : string.Empty) + "[grey].[/]");
    }

    // The per-row causes live in the report; the envelope carries the count, the rows it covers and the
    // first cause, the way an aborted sweep does.
    private static string AdoptionFailure(IReadOnlyList<WorkspaceRepairFailedAdoptionEntry> failed) =>
        $"Could not adopt {failed.Count} orphaned workspace store(s) into the registry " +
        $"({string.Join(", ", failed.Select(row => row.StoreKey))}). Cause: {failed[0].Cause}";

    private static IReadOnlyList<WorkspaceRepairHuskEntry> HuskSection(
        IReadOnlyList<WorkspaceRepairStaleEntry> huskRows) =>
        [.. huskRows.Select(husk => new WorkspaceRepairHuskEntry(
            husk.Name, husk.StoreKey, husk.RootPath, husk.StoreBytes, husk.StoreState))];

    private static void RenderReport(
        IReadOnlyList<WorkspaceRepairAdoptedEntry> adopted,
        IReadOnlyList<WorkspaceRepairNeedsRootEntry> needsRoot,
        IReadOnlyList<WorkspaceRepairFailedAdoptionEntry> failedAdoptions,
        IReadOnlyList<WorkspaceRepairStaleEntry> candidates,
        bool preview)
    {
        if (adopted.Count == 0 && needsRoot.Count == 0 && failedAdoptions.Count == 0 && candidates.Count == 0)
        {
            AnsiConsole.MarkupLine("Nothing to repair: no orphaned workspace stores found.");
            return;
        }

        if (preview)
        {
            AnsiConsole.MarkupLine("[grey]Dry run: nothing adopted, nothing deleted.[/]");
        }

        foreach (var entry in adopted)
        {
            AnsiConsole.MarkupLine(
                $"  [green]+[/] {(preview ? "would adopt" : "adopted")} {entry.Name.EscapeMarkup()} " +
                $"({entry.StoreKey.EscapeMarkup()}) → {entry.RootPath.EscapeMarkup()}");
        }

        foreach (var orphan in needsRoot)
        {
            var named = $"{orphan.Name.EscapeMarkup()} ({orphan.StoreKey.EscapeMarkup()})";
            AnsiConsole.MarkupLine(orphan.Reason switch
            {
                "root-gone" =>
                    $"  [yellow]![/] root gone: {named} — recorded root {orphan.RootPath!.EscapeMarkup()} " +
                    "does not exist; repair adopts only a live root, restore the directory and re-run",
                _ =>
                    $"  [yellow]![/] needs root: {named} — " +
                    $"re-run with --store-key {orphan.StoreKey.EscapeMarkup()} --root <path>",
            });
        }

        foreach (var failure in failedAdoptions)
        {
            AnsiConsole.MarkupLine(
                $"  [red]x[/] not adopted: {failure.Name.EscapeMarkup()} ({failure.StoreKey.EscapeMarkup()}) — " +
                $"{failure.Cause.EscapeMarkup()}");
        }

        foreach (var row in candidates.Where(row => row.Category == WorkspaceRepairPruner.CategoryStale))
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]-[/] stale: {row.Name.EscapeMarkup()} ({row.StoreKey.EscapeMarkup()}) " +
                $"root {row.RootPath.EscapeMarkup()}{LaneMarker(row)} · {StoreSizeLabel(row)}");
        }

        foreach (var row in candidates.Where(row => row.Category == WorkspaceRepairPruner.CategoryHusk))
        {
            AnsiConsole.MarkupLine(
                $"  [yellow]-[/] husk: {row.Name.EscapeMarkup()} ({row.StoreKey.EscapeMarkup()}) " +
                $"root {row.RootPath.EscapeMarkup()} · {StoreSizeLabel(row)}");
        }
    }

    // The category line lands after the sweep so the numbers are facts about this run, not intentions:
    // a dry run states what a confirmed run would prune, a decline states zeros because nothing was
    // deleted, an abort counts only the tail it actually reached, and unsafe keys never count as pruned.
    // A run with nothing to repair keeps its single line — zeros would dress up "nothing" as a report.
    // The tense follows the mode of the call, not of the sweep: a dry run that found nothing to prune
    // still previews an adoption it did not perform.
    private static void RenderSummary(
        int adopted,
        int needsRoot,
        IReadOnlyList<WorkspaceRepairStaleEntry> candidates,
        WorkspaceRepairPruneSummary prune,
        bool preview)
    {
        if (prune.Mode == WorkspaceRepairPruner.ModeNone && adopted == 0 && needsRoot == 0)
        {
            return;
        }

        var (stale, husks) = CategoryNumbers(candidates, prune, preview);
        AnsiConsole.MarkupLine(
            $"Repair report: [green]{adopted} {(preview ? "adoptable" : "adopted")}[/], " +
            $"[yellow]{needsRoot} {(preview ? "not adoptable" : "not adopted")}[/], " +
            $"{stale} stale row(s) {(preview ? "would be pruned" : "pruned")}, " +
            $"{husks} husk row(s) {(preview ? "would be pruned" : "pruned")}.");
    }

    private static WorkspaceRepairCategoryCounts Counts(
        int adopted,
        int needsRoot,
        IReadOnlyList<WorkspaceRepairStaleEntry> candidates,
        WorkspaceRepairPruneSummary prune,
        bool preview)
    {
        var (stalePruned, husksPruned) = CategoryNumbers(candidates, prune, preview);
        return new WorkspaceRepairCategoryCounts(adopted, needsRoot, stalePruned, husksPruned);
    }

    private static (int Stale, int Husks) CategoryNumbers(
        IReadOnlyList<WorkspaceRepairStaleEntry> candidates,
        WorkspaceRepairPruneSummary prune,
        bool preview)
    {
        return preview
            ? (CategoryCount(candidates, WorkspaceRepairPruner.CategoryStale),
                CategoryCount(candidates, WorkspaceRepairPruner.CategoryHusk))
            : (CountPruned(prune, WorkspaceRepairPruner.CategoryStale),
                CountPruned(prune, WorkspaceRepairPruner.CategoryHusk));
    }

    private static int CategoryCount(IReadOnlyList<WorkspaceRepairStaleEntry> candidates, string category) =>
        candidates.Count(row => row.Category == category);

    private static int CountPruned(WorkspaceRepairPruneSummary prune, string category) =>
        prune.Rows.Count(row => row.Category == category && WorkspaceRepairPruner.IsPruned(row));

    private static void RenderPrune(WorkspaceRepairPruneSummary prune, IReadOnlyList<WorkspaceRepairStaleEntry> candidates)
    {
        switch (prune.Mode)
        {
            case WorkspaceRepairPruner.ModeNone:
                return;
            case WorkspaceRepairPruner.ModeDryRun:
                AnsiConsole.MarkupLine(
                    $"Dry run: {WorkspaceRepairPruner.CandidateLabel(candidates)}, " +
                    $"{DryRunStoresText(candidates, prune.TotalStoreBytes)}. Nothing was deleted.");
                AnsiConsole.MarkupLine($"[grey]{BackupAdvice().EscapeMarkup()}[/]");
                return;
            case WorkspaceRepairPruner.ModeRefused:
                AnsiConsole.MarkupLine(
                    $"[yellow]Not pruned:[/] {WorkspaceRepairPruner.CandidateLabel(candidates)} left in the registry.");
                AnsiConsole.MarkupLine($"[grey]{BackupAdvice().EscapeMarkup()}[/]");
                return;
            case WorkspaceRepairPruner.ModeDeclined:
                AnsiConsole.MarkupLine(
                    $"[yellow]Prune declined:[/] {WorkspaceRepairPruner.CandidateLabel(candidates)} " +
                    "kept in the registry, nothing was deleted.");
                return;
        }

        foreach (var row in prune.Rows)
        {
            AnsiConsole.MarkupLine(PruneLine(row));
        }

        var stores = prune.StoresRemoved;
        var held = prune.Rows.Count(row => row.Action == WorkspaceRepairPruner.ActionSkippedUnsafeKey);

        if (prune.Mode == WorkspaceRepairPruner.ModeAborted)
        {
            var notAttempted = candidates.Count - prune.Rows.Count;
            AnsiConsole.MarkupLine(
                $"[red]Sweep stopped:[/] {SweepFailure(prune).EscapeMarkup()} — " +
                $"{WorkspaceRepairPruner.PrunedLabel(prune.Rows)} already removed, " +
                $"{stores} store dir(s) gone" +
                (notAttempted > 0 ? $", {notAttempted} row(s) not attempted" : string.Empty) + ".");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[green]Pruned {WorkspaceRepairPruner.PrunedLabel(prune.Rows)}[/] · {RemovedStoresText(stores, prune.TotalStoreBytes)}" +
                (held > 0 ? $" · [yellow]{held} left alone[/]" : string.Empty) + ".");
        }

        if (prune.Rows.Any(WorkspaceRepairPruner.IsPruned))
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
            WorkspaceRepairPruner.ActionStoreRemovedRowKept =>
                $"  [red]x[/] removed: unverified — store dir gone, row kept: {named} — {FailureText(row.Failure)}",
            WorkspaceRepairPruner.ActionSkippedUnsafeKey =>
                $"  [red]![/] left alone, store key is not a single path segment: {named}",
            _ => $"  [red]![/] left alone ({row.Action.EscapeMarkup()}): {named}",
        };
    }

    private static string SizeParenthesis(long? bytes) =>
        bytes is { } value ? $" ({StoreSizeText.Format(value)})" : string.Empty;

    // The dead root of a lane and the dead root of a plain folder are the same fact to this sweep, but a
    // reader deciding whether to confirm says "lane" and looks at the parent, so the line names it.
    private static string LaneMarker(WorkspaceRepairStaleEntry row) =>
        row.ParentRootPath is { } parent
            ? $" — worktree child of {parent.EscapeMarkup()}"
            : string.Empty;

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

    private static string PruneRefusal(string label, bool json) =>
        $"Refusing to prune {label} without confirmation: " +
        (json
            ? "--json writes one machine-readable document to stdout, so a prompt has no channel here"
            : "this is not an interactive terminal") +
        $", and the absence of a prompt is not consent. Re-run with --dry-run to list them, or --yes to confirm; " +
        $"{BackupAdvice()}.";

    private static void WriteHuskHints(IReadOnlyList<WorkspaceRepairStaleEntry> husks, WorkspaceRepairPruneSummary prune)
    {
        foreach (var husk in husks.Where(husk => StillRegistered(husk, prune)))
        {
            Console.Error.WriteLine(
                $"warning: workspace '{husk.Name}' looks like a husk (storeKey '{husk.StoreKey}', " +
                $"root path '{husk.RootPath}'); run 'squeezr drop --store-key {husk.StoreKey}' " +
                "to remove this entry and its store.");
        }
    }

    // The drop advice is only for husks this run did not prune away: a row that is already gone must not
    // be told to run drop on itself. Failed, skipped and unattempted husks keep the advice.
    private static bool StillRegistered(WorkspaceRepairStaleEntry husk, WorkspaceRepairPruneSummary prune) =>
        prune.Rows.FirstOrDefault(row => row.StoreKey == husk.StoreKey) is not { } row
        || !WorkspaceRepairPruner.IsPruned(row);
}
