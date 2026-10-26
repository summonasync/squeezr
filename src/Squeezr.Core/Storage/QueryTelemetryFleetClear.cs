namespace Squeezr.Core.Storage;

public static class QueryTelemetryFleetClear
{
    // Same fleet definition as the usage rollup: every WorkspaceRegistry row, stale ones named as
    // skipped instead of shown as stale. The only path this builds is SqueezrPaths.QueriesLogPath -
    // the store dir string is used solely to take the telemetry write lock, never for a delete.
    public static FleetTelemetryClearResult Clear()
    {
        var staleKeys = UsageRollupLoader.StaleStoreKeys();
        var cleared = new List<FleetTelemetryClearResult.ClearedStore>();
        var skipped = new List<FleetTelemetryClearResult.SkippedStore>();
        foreach (var entry in WorkspaceRegistry.Load())
        {
            if (staleKeys.Contains(entry.StoreKey))
            {
                skipped.Add(new(entry.StoreKey, "root gone"));
                continue;
            }

            var logPath = SqueezrPaths.QueriesLogPath(entry.RootPath);
            if (!File.Exists(logPath))
            {
                cleared.Add(new(entry.StoreKey, 0));
                continue;
            }

            using var writer = TelemetryWriteLock.TryAcquire(SqueezrPaths.StoreDir(entry.RootPath));
            if (writer is null)
            {
                skipped.Add(new(entry.StoreKey, "telemetry write lock busy"));
                continue;
            }

            var removed = QueryTelemetry.Count(entry.RootPath);
            File.Delete(logPath);
            cleared.Add(new(entry.StoreKey, removed));
        }

        return new FleetTelemetryClearResult(cleared, skipped, cleared.Sum(item => item.RecordsRemoved));
    }
}

public sealed record FleetTelemetryClearResult(
    IReadOnlyList<FleetTelemetryClearResult.ClearedStore> Cleared,
    IReadOnlyList<FleetTelemetryClearResult.SkippedStore> Skipped,
    int TotalRemoved)
{
    public sealed record ClearedStore(string StoreKey, int RecordsRemoved);

    public sealed record SkippedStore(string StoreKey, string Reason);
}
