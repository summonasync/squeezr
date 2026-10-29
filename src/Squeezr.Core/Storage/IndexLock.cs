using System.Globalization;
using System.Text;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Storage;

public sealed class IndexLock : IDisposable
{
    private const int OwnerPidMaxBytes = 32;

    private readonly FileStream _stream;
    private readonly string _path;

    private IndexLock(FileStream stream, string path)
    {
        _stream = stream;
        _path = path;
    }

    public static IndexLock Acquire(string storeDir, Action<string>? onStaleTakeover = null)
    {
        var path = Path.Combine(storeDir, SqueezrPaths.LockFileName);
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            return TakeOverStale(path, onStaleTakeover);
        }

        WriteOwnerPid(stream);
        return new IndexLock(stream, path);
    }

    private static IndexLock TakeOverStale(string path, Action<string>? onStaleTakeover)
    {
        int? ownerPid;
        try
        {
            ownerPid = ReadOwnerPid(path);
        }
        catch (IOException ex)
        {
            throw new WorkspaceLockedException(
                "Workspace is locked, but the lock file itself cannot be read to identify the holder. " +
                "Inspect 'squeezr fleet' and stop the holder with 'squeezr fleet stop <store-key>'; " +
                "do not delete index.lock by hand.", ex);
        }

        if (ownerPid is int alivePid && DaemonHeartbeatReader.IsProcessAlive(alivePid))
        {
            throw new WorkspaceLockedException(DescribeHolder(path, alivePid));
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.None);
        }
        catch (IOException)
        {
            throw ownerPid is int winnerPid
                ? new WorkspaceLockedException(DescribeHolder(path, winnerPid))
                : new WorkspaceLockedException();
        }

        var owner = ownerPid is int deadPid
            ? deadPid.ToString(CultureInfo.InvariantCulture)
            : "unknown";
        onStaleTakeover?.Invoke($"stale lock from pid {owner} removed");
        WriteOwnerPid(stream);
        return new IndexLock(stream, path);
    }

    private static void WriteOwnerPid(FileStream stream)
    {
        try
        {
            var pid = Encoding.UTF8.GetBytes(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            stream.Write(pid, 0, pid.Length);
            stream.Flush();
        }
        catch (IOException)
        {
            stream.Dispose();
            throw;
        }
    }

    private static string DescribeHolder(string lockPath, int ownerPid)
    {
        var storeDir = Path.GetDirectoryName(lockPath) ?? string.Empty;
        string lockAge;
        try
        {
            var heldFor = DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath);
            lockAge = heldFor >= TimeSpan.Zero
                ? $"{(long)heldFor.TotalSeconds}s"
                : "unknown";
        }
        catch (IOException)
        {
            lockAge = "unknown";
        }

        var report = new StringBuilder($"Workspace is locked by another squeezr process: pid {ownerPid}, lock held for {lockAge}.");
        report.Append(DescribeHeartbeat(storeDir, ownerPid));
        report.Append(DescribePassPhase(storeDir, ownerPid));
        report.Append(
            " If the holder is wedged — a beating heartbeat does not mean it is making progress — inspect 'squeezr fleet' " +
            "and stop it with 'squeezr fleet stop <store-key>'; otherwise wait for the pass to finish. " +
            "Do not delete index.lock by hand: an unblocked wedged pass would keep writing without mutual exclusion.");
        return report.ToString();
    }

    private static string DescribeHeartbeat(string storeDir, int ownerPid)
    {
        foreach (var kind in (string[])[DaemonHeartbeat.DefaultKind, DaemonHeartbeat.ServeWatchKind])
        {
            var beat = DaemonHeartbeatReader.TryRead(storeDir, kind);
            if (beat is null || beat.Pid != ownerPid)
            {
                continue;
            }

            var beatAge = Math.Max(0, (long)(DateTime.UtcNow - beat.LastBeatAt).TotalSeconds);
            return $" Holder heartbeat '{kind}' beat {beatAge}s ago (daemon started {beat.StartedAt:u}).";
        }

        return " No holder heartbeat in the store (the holder predates heartbeat files or is not a daemon).";
    }

    private static string DescribePassPhase(string storeDir, int ownerPid)
    {
        var pass = PassPhaseStampFile.TryRead(storeDir);
        if (pass is null || pass.Pid != ownerPid)
        {
            return string.Empty;
        }

        return pass.Phase switch
        {
            PassPhaseStamp.StuckPhase =>
                $" Its last pass reported itself STUCK (started {pass.StartedAt:u}, no progress recorded since) — " +
                "this is the alive-but-wedged shape, not a busy daemon.",
            PassPhaseStamp.RunningPhase =>
                $" Its last pass started {pass.StartedAt:u} and has not finished.",
            _ => string.Empty,
        };
    }

    private static int? ReadOwnerPid(string path)
    {
        // A live holder keeps its handle open, so this is the one read that must not claim a lock: the
        // managed form only answers once the raw probe is unavailable, and then the caller degrades.
        if (RawOpenRead.TryReadAllText(path, OwnerPidMaxBytes, out var owner))
        {
            return ParseOwnerPid(owner);
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream);
        return ParseOwnerPid(reader.ReadToEnd().Trim());
    }

    private static int? ParseOwnerPid(string owner) =>
        int.TryParse(owner, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
            ? pid
            : null;

    public void Dispose()
    {
        _stream.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}
