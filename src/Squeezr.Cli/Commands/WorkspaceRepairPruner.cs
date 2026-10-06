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
    public const string ActionWouldPrune = "would-prune";
    public const string ActionRowAndStore = "pruned-row-and-store";
    public const string ActionRowOnly = "pruned-row-only";
    public const string ActionSkippedUnsafeKey = "skipped-unsafe-store-key";
    public const string ActionPruneFailed = "prune-failed";

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
            .Select(Measured)
            .ToList();

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

        if (gate == RepairPruneGate.Prompt && !await ConfirmAsync(candidates.Count, console, cancellationToken))
        {
            return new WorkspaceRepairPruneSummary(ModeDeclined, [], 0, 0);
        }

        return Sweep(candidates);
    }

    // The sweep dies on the first store it cannot remove. Rows removed before that point are already
    // gone from the registry, so they are reported instead of being lost with the exception.
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
        int candidateCount,
        IAnsiConsole console,
        CancellationToken cancellationToken)
    {
        var prompt = new ConfirmationPrompt($"Prune {candidateCount} stale row(s) and their store dirs?")
        {
            DefaultValue = false,
        };
        return await prompt.ShowAsync(console, cancellationToken);
    }

    private static WorkspaceRepairStaleEntry Measured(StaleInfo row)
    {
        var (state, bytes) = MeasureStore(row.StoreKey);
        return new WorkspaceRepairStaleEntry(row.Name, row.StoreKey, row.RootPath, bytes, state);
    }

    private static WorkspaceRepairPrunedEntry Planned(WorkspaceRepairStaleEntry candidate) =>
        new(candidate.StoreKey, candidate.Name, candidate.RootPath, CategoryStale, ActionWouldPrune, candidate.StoreBytes, []);

    private static WorkspaceRepairPrunedEntry Drop(WorkspaceRepairStaleEntry candidate)
    {
        if (!IsSingleSegmentKey(candidate.StoreKey))
        {
            return new WorkspaceRepairPrunedEntry(
                candidate.StoreKey,
                candidate.Name,
                candidate.RootPath,
                CategoryStale,
                ActionSkippedUnsafeKey,
                null,
                []);
        }

        var removed = WorkspaceStore.DropByStoreKey(candidate.StoreKey, StaleLockWarningSink.ToStderr);

        return new WorkspaceRepairPrunedEntry(
            candidate.StoreKey,
            candidate.Name,
            candidate.RootPath,
            CategoryStale,
            removed.Count > 0 ? ActionRowAndStore : ActionRowOnly,
            candidate.StoreBytes,
            removed);
    }

    private static WorkspaceRepairPrunedEntry Failed(WorkspaceRepairStaleEntry candidate, Exception ex) =>
        new(
            candidate.StoreKey,
            candidate.Name,
            candidate.RootPath,
            CategoryStale,
            ActionPruneFailed,
            candidate.StoreBytes,
            [],
            DescribeFailure(ex));

    // Exception messages here carry the store path, and the sweep must not name a path it did not delete.
    private static string DescribeFailure(Exception ex) =>
        ex is WorkspaceLockedException locked
            ? locked.Message
            : $"{ex.GetType().Name}: a store dir could not be removed";

    private static long RemovedStoreBytes(IReadOnlyList<WorkspaceRepairPrunedEntry> rows) =>
        rows.Where(row => row.Action == ActionRowAndStore).Sum(row => row.StoreBytes ?? 0);

    private static int RemovedStores(IReadOnlyList<WorkspaceRepairPrunedEntry> rows) =>
        rows.Count(row => row.Action == ActionRowAndStore);

    private static (string State, long? Bytes) MeasureStore(string storeKey)
    {
        if (!IsSingleSegmentKey(storeKey))
        {
            return (StoreNotMeasured, null);
        }

        var storeDir = Path.Combine(SqueezrHome.WorkspacesDir, storeKey);
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

    // DropByStoreKey resolves workspaces/<storeKey> recursively, so a hand-edited registry row carrying
    // a traversal key would delete outside the store root. Repair sweeps whole categories at once and
    // refuses such keys instead of feeding them to the deletion primitive; drop --store-key stays as-is.
    // The guard is about containment, not charset: WorkspaceStoreKeys.Slug emits non-ASCII letters, so an
    // alphabet check would reject rows this command is supposed to prune and be stricter than drop.
    private static bool IsSingleSegmentKey(string storeKey)
    {
        if (storeKey.Length == 0 || storeKey is "." or ".." || storeKey.IndexOfAny('/', '\\', '\0') >= 0)
        {
            return false;
        }

        var workspaces = SqueezrHome.WorkspacesDir;
        var resolved = Path.GetFullPath(Path.Combine(workspaces, storeKey));
        var root = Path.TrimEndingDirectorySeparator(workspaces) + Path.DirectorySeparatorChar;
        return resolved.Length > root.Length
               && resolved.StartsWith(root, StringComparison.Ordinal)
               && resolved.IndexOf(Path.DirectorySeparatorChar, root.Length, resolved.Length - root.Length) < 0;
    }
}
