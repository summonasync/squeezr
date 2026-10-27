using System.Text.Json;
using Squeezr.Core.Serialization;

namespace Squeezr.Core.Indexing.Watch;

internal static class WatchOwnerMarkerFile
{
    /// <summary>
    /// true means this call wrote the file; false means somebody got there first. Exclusivity is the OS
    /// CreateNew guarantee, not a check we do before writing. A missing store directory rethrows instead of
    /// reporting a lost race — there is no store to claim in that case.
    /// </summary>
    public static bool TryCreate(string path, WatchOwnerMarker marker)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(stream, marker, SqueezrJson.FileOptions);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            throw;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static void Overwrite(string path, WatchOwnerMarker marker)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(marker, SqueezrJson.FileOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = TryDelete(tempPath);
            throw;
        }
    }

    public static (WatchOwnerMarkerState State, WatchOwnerMarker? Marker) Read(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return (WatchOwnerMarkerState.Absent, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (WatchOwnerMarkerState.Unreadable, null);
        }

        try
        {
            var marker = JsonSerializer.Deserialize<WatchOwnerMarker>(json, SqueezrJson.FileOptions);
            return marker is { } parsed && IsWellFormed(parsed)
                ? (WatchOwnerMarkerState.Present, parsed)
                : (WatchOwnerMarkerState.Unreadable, null);
        }
        catch (JsonException)
        {
            return (WatchOwnerMarkerState.Unreadable, null);
        }
    }

    public static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reaps a marker only while it still names the owner the caller evaluated: a takeover may never
    /// delete a claim that changed hands in between, because the process that won it is live. An absent
    /// file answers true — there is nothing left to protect and the caller's create still arbitrates.
    /// </summary>
    public static bool TryDeleteIf(string path, WatchOwnerMarker expected)
    {
        var (state, marker) = Read(path);
        if (state == WatchOwnerMarkerState.Absent)
        {
            return true;
        }

        return marker == expected && TryDelete(path);
    }

    public static DateTime? TryLastWrittenUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsWellFormed(WatchOwnerMarker marker) =>
        marker.Pid > 0
        && !string.IsNullOrWhiteSpace(marker.Host)
        && !string.IsNullOrWhiteSpace(marker.Kind)
        && marker.StartedAt != default
        && marker.LastBeatAt != default;
}
