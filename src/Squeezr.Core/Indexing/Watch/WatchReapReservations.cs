namespace Squeezr.Core.Indexing.Watch;

internal static class WatchReapReservations
{
    /// <summary>
    /// Deletes every reap reservation a crashed process left between renaming the marker aside and deleting
    /// or restoring it. Reservations only ever exist under the .reap suffix and only ever hold a marker that
    /// was already evaluated as reapable, so sweeping can neither resurrect a claim nor reach the live
    /// marker — deleting is the only thing a sweep ever does to a file. Reservations younger than the stale
    /// window are left alone: they may belong to a live peer's reap still in flight, and that peer deletes
    /// its own reservation microseconds after creating it. The swept markers come back so the caller can
    /// still name who the store was taken from when the reservation is all that remains of the displaced
    /// owner.
    /// </summary>
    public static IReadOnlyList<WatchOwnerMarker> Sweep(string markerPath, Action<string>? onWarning)
    {
        var swept = new List<WatchOwnerMarker>();
        var storeDir = Path.GetDirectoryName(markerPath);
        if (storeDir is null)
        {
            return swept;
        }

        string[] reservations;
        try
        {
            reservations = Directory.GetFiles(storeDir, $"{Path.GetFileName(markerPath)}.*.reap");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onWarning?.Invoke($"reap reservations could not be listed: {ex.Message}");
            return swept;
        }

        foreach (var reservation in reservations)
        {
            if (WrittenWithinStaleWindow(reservation))
            {
                continue;
            }

            var (state, marker) = WatchOwnerMarkerFile.Read(reservation);
            if (state == WatchOwnerMarkerState.Present && marker is { } remnant)
            {
                swept.Add(remnant);
            }

            if (!WatchOwnerMarkerFile.TryDelete(reservation))
            {
                onWarning?.Invoke($"reap reservation could not be removed: {Path.GetFileName(reservation)}");
            }
        }

        return swept;
    }

    private static bool WrittenWithinStaleWindow(string reservation)
    {
        try
        {
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(reservation) <= DaemonHeartbeat.StaleThreshold;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
