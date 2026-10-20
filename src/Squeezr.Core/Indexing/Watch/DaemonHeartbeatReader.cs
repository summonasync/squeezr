using System.Diagnostics;
using System.Text.Json;
using Squeezr.Core.Serialization;

namespace Squeezr.Core.Indexing.Watch;

public static class DaemonHeartbeatReader
{
    public static DaemonHeartbeat? TryRead(string storeDir, string kind = DaemonHeartbeat.DefaultKind)
    {
        try
        {
            var json = File.ReadAllText(DaemonHeartbeat.PathFor(storeDir, kind));
            return JsonSerializer.Deserialize<DaemonHeartbeat>(json, SqueezrJson.FileOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public static bool IsAlive(
        DaemonHeartbeat heartbeat,
        DateTime utcNow,
        Func<int, bool>? isProcessAlive = null)
    {
        if (utcNow - heartbeat.LastBeatAt > DaemonHeartbeat.StaleThreshold)
        {
            return false;
        }
        return (isProcessAlive ?? IsProcessAlive)(heartbeat.Pid);
    }

    public static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
