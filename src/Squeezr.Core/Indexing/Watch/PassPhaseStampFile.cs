using System.Text.Json;
using Squeezr.Core.Serialization;

namespace Squeezr.Core.Indexing.Watch;

public static class PassPhaseStampFile
{
    public static void Write(string storeDir, PassPhaseStamp stamp)
    {
        var path = PassPhaseStamp.PathFor(storeDir);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(stamp, SqueezrJson.FileOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tempPath);
        }
    }

    public static PassPhaseStamp? TryRead(string storeDir)
    {
        try
        {
            var json = File.ReadAllText(PassPhaseStamp.PathFor(storeDir));
            return JsonSerializer.Deserialize<PassPhaseStamp>(json, SqueezrJson.FileOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // A leftover record is stale bookkeeping only when it describes work that cannot finish: a writer that
    // is gone (or this very process, restarted) for a running/stuck pass, and an interrupted hand-off in
    // any case - a pass the shutdown cut short is unfinished by definition, whoever wrote it, and it is a
    // to-do note rather than evidence. A running or stuck record naming a live writer is that daemon's
    // wedge evidence and is never touched: erasing it here is how a stuck pass hides again behind a
    // clean-looking store. Returns true when a record of unfinished work was consumed.
    public static bool ClearIfWriterGone(string storeDir, Func<int, bool>? isProcessAlive = null)
    {
        var leftover = TryRead(storeDir);
        if (leftover is null)
        {
            return false;
        }

        var handOff = leftover.Phase == PassPhaseStamp.InterruptedPhase;
        var writerIsUs = leftover.Pid == Environment.ProcessId;
        if (!handOff && !writerIsUs && (isProcessAlive ?? DaemonHeartbeatReader.IsProcessAlive)(leftover.Pid))
        {
            return false;
        }

        Clear(storeDir);
        return true;
    }

    public static void Clear(string storeDir)
    {
        try
        {
            File.Delete(PassPhaseStamp.PathFor(storeDir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
