using Spectre.Console;
using Squeezr.Cli.Output;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

internal enum RepairPruneGate
{
    None,
    DryRun,
    Confirmed,
    Prompt,
    Refuse,
}

internal static class WorkspaceRepairPruner
{
    public const string ModeNone = "none";
    public const string ModeDryRun = "dry-run";
    public const string ModePruned = "pruned";
    public const string ModeDeclined = "declined";
    public const string ModeRefused = "refused";
    public const string ModeAborted = "aborted";

    public const string CategoryStale = "stale";
    public const string CategoryHusk = "husk";
    public const string ActionWouldPrune = "would-prune";
    public const string ActionRowAndStore = "pruned-row-and-store";
    public const string ActionRowOnly = "pruned-row-only";
    public const string ActionSkippedUnsafeKey = "skipped-unsafe-store-key";
    public const string ActionPruneFailed = "prune-failed";
    public const string ActionStoreRemovedRowKept = "store-removed-row-kept";

    public const string StoreMeasured = "measured";
    public const string StoreMissing = "missing";
    public const string StoreUnreadable = "unreadable";
    public const string StoreNotMeasured = "not-measured";

    public static RepairPruneGate Gate(int candidateCount, bool dryRun, bool yes, bool promptAvailable)
    {
        if (candidateCount == 0)
        {
            return RepairPruneGate.None;
        }

        if (dryRun)
        {
            return RepairPruneGate.DryRun;
        }

        if (yes)
        {
            return RepairPruneGate.Confirmed;
        }

        return promptAvailable ? RepairPruneGate.Prompt : RepairPruneGate.Refuse;
    }

    public static IReadOnlyList<WorkspaceRepairStaleEntry> Candidates(
        IReadOnlyList<StaleInfo> stale,
        IReadOnlySet<string> huskKeys) =>
        stale
            .Where(row => !huskKeys.Contains(row.StoreKey))
            .OrderBy(row => row.Name, StringComparer.Ordinal)
            .ThenBy(row => row.StoreKey, StringComparer.Ordinal)
            .Select(row => Measured(row.Name, row.StoreKey, row.RootPath, CategoryStale))
            .ToList();

    public static IReadOnlyList<WorkspaceRepairStaleEntry> HuskCandidates(
        IReadOnlyList<HuskInfo> husks,
        IReadOnlySet<string> staleKeys) =>
        husks
            .Where(husk => !staleKeys.Contains(husk.StoreKey))
            .OrderBy(husk => husk.Name, StringComparer.Ordinal)
            .ThenBy(husk => husk.StoreKey, StringComparer.Ordinal)
            .Select(husk => Measured(husk.Name, husk.StoreKey, husk.RootPath, CategoryHusk))
            .ToList();

    public static string CandidateLabel(IReadOnlyList<WorkspaceRepairStaleEntry> candidates) =>
        Label(
            candidates.Count(row => row.Category == CategoryStale),
            candidates.Count(row => row.Category == CategoryHusk));

    public static string PrunedLabel(IReadOnlyList<WorkspaceRepairPrunedEntry> rows) =>
        Label(
            rows.Count(row => row.Category == CategoryStale && IsPruned(row)),
            rows.Count(row => row.Category == CategoryHusk && IsPruned(row)));

    private static string Label(int stale, int husks) =>
        (stale, husks) switch
        {
            (0, 0) => "0 row(s)",
            (_, 0) => $"{stale} stale row(s)",
            (0, _) => $"{husks} husk row(s)",
            _ => $"{stale + husks} row(s) ({stale} stale, {husks} husk)",
        };

    public static async Task<WorkspaceRepairPruneSummary> RunAsync(
        RepairPruneGate gate,
        IReadOnlyList<WorkspaceRepairStaleEntry> candidates,
        IAnsiConsole console,
        CancellationToken cancellationToken)
    {
        if (gate == RepairPruneGate.None)
        {
            return new WorkspaceRepairPruneSummary(ModeNone, [], 0, 0);
        }

        if (gate == RepairPruneGate.DryRun)
        {
            return new WorkspaceRepairPruneSummary(
                ModeDryRun,
                [.. candidates.Select(Planned)],
                candidates.Sum(row => row.StoreBytes ?? 0),
                0);
        }

        if (gate == RepairPruneGate.Refuse)
        {
            return new WorkspaceRepairPruneSummary(ModeRefused, [], 0, 0);
        }

        if (gate == RepairPruneGate.Prompt && !await ConfirmAsync(candidates, console, cancellationToken))
        {
            return new WorkspaceRepairPruneSummary(ModeDeclined, [], 0, 0);
        }

        return Sweep(candidates);
    }

    // The sweep dies on the first store it cannot remove. Rows removed before that point are already
    // gone from the registry, so they are reported instead of being lost with the exception. A registry
    // write that dies after a store dir disappeared keeps those paths: the store fact and the row fact
    // point in different directions, and the report has to say both.
    private static WorkspaceRepairPruneSummary Sweep(IReadOnlyList<WorkspaceRepairStaleEntry> candidates)
    {
        var rows = new List<WorkspaceRepairPrunedEntry>();
        foreach (var candidate in candidates)
        {
            try
            {
                rows.Add(Drop(candidate));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WorkspaceLockedException)
            {
                rows.Add(Failed(candidate, ex));
                return new WorkspaceRepairPruneSummary(
                    ModeAborted, rows, RemovedStoreBytes(rows), RemovedStores(rows));
            }
        }

        return new WorkspaceRepairPruneSummary(ModePruned, rows, RemovedStoreBytes(rows), RemovedStores(rows));
    }

    private static async Task<bool> ConfirmAsync(
        IReadOnlyList<WorkspaceRepairStaleEntry> candidates,
        IAnsiConsole console,
        CancellationToken cancellationToken)
    {
        var prompt = new ConfirmationPrompt($"Prune {CandidateLabel(candidates)} and their store dirs?")
        {
            DefaultValue = false,
        };
        return await prompt.ShowAsync(console, cancellationToken);
    }

    private static WorkspaceRepairStaleEntry Measured(string name, string storeKey, string rootPath, string category)
    {
        var (state, bytes) = MeasureStore(storeKey);
        return new WorkspaceRepairStaleEntry(name, storeKey, rootPath, bytes, state, category);
    }

    private static WorkspaceRepairPrunedEntry Planned(WorkspaceRepairStaleEntry candidate) =>
        new(candidate.StoreKey, candidate.Name, candidate.RootPath, candidate.Category, ActionWouldPrune, candidate.StoreBytes, []);

    private static WorkspaceRepairPrunedEntry Drop(WorkspaceRepairStaleEntry candidate)
    {
        if (!WorkspaceStorePaths.IsSingleSegmentKey(candidate.StoreKey))
        {
            return new WorkspaceRepairPrunedEntry(
                candidate.StoreKey,
                candidate.Name,
                candidate.RootPath,
                candidate.Category,
                ActionSkippedUnsafeKey,
                null,
                []);
        }

        var removed = WorkspaceStore.DropByStoreKey(candidate.StoreKey, StaleLockWarningSink.ToStderr);

        return new WorkspaceRepairPrunedEntry(
            candidate.StoreKey,
            candidate.Name,
            candidate.RootPath,
            candidate.Category,
            removed.Count > 0 ? ActionRowAndStore : ActionRowOnly,
            candidate.StoreBytes,
            removed);
    }

    private static WorkspaceRepairPrunedEntry Failed(WorkspaceRepairStaleEntry candidate, Exception ex) =>
        new(
            candidate.StoreKey,
            candidate.Name,
            candidate.RootPath,
            candidate.Category,
            ex is WorkspaceStoreRemovalUnverifiedException ? ActionStoreRemovedRowKept : ActionPruneFailed,
            candidate.StoreBytes,
            (ex as WorkspaceStoreRemovalUnverifiedException)?.RemovedStoreDirs ?? [],
            DescribeFailure(ex));

    // Exception messages here carry the store path, and the sweep must not name a path it did not delete.
    // The removal primitive tags both removal phases, so the wording follows the tag: a store-side tag
    // means nothing disappeared, an unverified tag means a store dir did disappear and the row did not.
    private static string DescribeFailure(Exception ex) => ex switch
    {
        WorkspaceLockedException locked => locked.Message,
        WorkspaceStoreRemovalException removal => $"{InnerType(removal)}: a store dir could not be removed",
        WorkspaceStoreRemovalUnverifiedException unverified =>
            $"{InnerType(unverified)}: the store dir was removed, the registry row could not be pruned",
        _ => $"{ex.GetType().Name}: the registry row could not be pruned",
    };

    private static string InnerType(Exception ex) =>
        (ex.InnerException ?? ex).GetType().Name;

    // Aggregates count paths that really disappeared, never an action label: a row that survived while
    // its store dir did not is one store dir gone, and a refusal that deleted nothing is zero.
    private static long RemovedStoreBytes(IReadOnlyList<WorkspaceRepairPrunedEntry> rows) =>
        rows.Where(RemovedAStore).Sum(row => row.StoreBytes ?? 0);

    private static int RemovedStores(IReadOnlyList<WorkspaceRepairPrunedEntry> rows) =>
        rows.Count(RemovedAStore);

    public static bool IsPruned(WorkspaceRepairPrunedEntry row) =>
        row.Action is ActionRowAndStore or ActionRowOnly;

    private static bool RemovedAStore(WorkspaceRepairPrunedEntry row) =>
        row.Action is ActionRowAndStore or ActionStoreRemovedRowKept;

    private static (string State, long? Bytes) MeasureStore(string storeKey)
    {
        if (!WorkspaceStorePaths.IsSingleSegmentKey(storeKey))
        {
            return (StoreNotMeasured, null);
        }

        var storeDir = WorkspaceStorePaths.RequireStoreDir(storeKey);
        if (!Directory.Exists(storeDir))
        {
            return (StoreMissing, null);
        }

        try
        {
            var total = 0L;
            foreach (var file in Directory.EnumerateFiles(storeDir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            return (StoreMeasured, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (StoreUnreadable, null);
        }
    }
}
