using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Core.Indexing.Watch.Supervision;

public sealed class WatchdLock : IDisposable
{
    private readonly string _path;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly Func<int, DateTime?> _processStartUtc;
    private WatchdLockOwner? _held;

    public WatchdLock() : this(WatchdPaths.LockPath)
    {
    }

    internal WatchdLock(
        string path,
        Func<int, bool>? isProcessAlive = null,
        Func<int, DateTime?>? processStartUtc = null)
    {
        _path = path;
        _isProcessAlive = isProcessAlive ?? DaemonHeartbeatReader.IsProcessAlive;
        _processStartUtc = processStartUtc ?? WatchdLockOwner.ReadLiveStartUtc;
    }

    public WatchdLockOutcome TryAcquire()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var current = ReadLock();
        if (current.Owner is { } owner && IsOwnerAlive(owner))
        {
            return new WatchdLockOutcome(false, owner.Pid);
        }

        if (current.Status == LockReadStatus.Unreadable || !TryStealStaleFile(current.Content))
        {
            return Refusal();
        }

        var claim = new WatchdLockOwner(
            Environment.ProcessId,
            Environment.MachineName,
            _processStartUtc(Environment.ProcessId));
        try
        {
            WriteOwner(claim);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Refusal();
        }

        _held = claim;
        return new WatchdLockOutcome(true, null);
    }

    public void Dispose()
    {
        if (_held is null)
        {
            return;
        }

        _held = null;
        if (ReadLock().Owner is { } owner && owner.ClaimedByCurrentProcess)
        {
            TryDelete();
        }
    }

    internal bool TryStealStaleFile(string? expectedContent)
    {
        if (expectedContent is null)
        {
            return true;
        }

        var stolen = $"{_path}.{Guid.NewGuid():N}.stolen";
        try
        {
            File.Move(_path, stolen);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        string actual;
        try
        {
            actual = File.ReadAllText(stolen);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PutBack(stolen);
            return false;
        }

        if (actual != expectedContent)
        {
            PutBack(stolen);
            return false;
        }

        TryCleanup(stolen);
        return true;
    }

    private void PutBack(string stolen)
    {
        try
        {
            File.Move(stolen, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(stolen);
            }
            catch (IOException)
            {
            }
        }
    }

    private void TryCleanup(string stolen)
    {
        try
        {
            File.Delete(stolen);
        }
        catch (IOException)
        {
        }
    }

    private WatchdLockOutcome Refusal() => new(false, ReadLock().Owner?.Pid);

    private LockRead ReadLock()
    {
        string content;
        try
        {
            content = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return LockRead.Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new LockRead(LockReadStatus.Unreadable, null, null);
        }

        try
        {
            return new LockRead(
                LockReadStatus.Present,
                content,
                JsonSerializer.Deserialize<WatchdLockOwner>(content, SqueezrJson.FileOptions));
        }
        catch (JsonException)
        {
            return new LockRead(LockReadStatus.Present, content, null);
        }
    }

    private bool IsOwnerAlive(WatchdLockOwner owner)
    {
        if (!owner.HeldOnThisHost)
        {
            return true;
        }

        if (!_isProcessAlive(owner.Pid))
        {
            return false;
        }

        return owner.CheckAgainstLiveStart(_processStartUtc(owner.Pid))
            != WatchdLockOwner.StartCheck.Drifted;
    }

    private void WriteOwner(WatchdLockOwner owner)
    {
        using var stream = new FileStream(_path, FileMode.CreateNew, FileAccess.Write);
        using var writer = new StreamWriter(stream);
        writer.Write(JsonSerializer.Serialize(owner, SqueezrJson.FileOptions));
    }

    private void TryDelete()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }

    private enum LockReadStatus
    {
        Missing,
        Present,
        Unreadable,
    }

    private sealed record LockRead(LockReadStatus Status, string? Content, WatchdLockOwner? Owner)
    {
        public static readonly LockRead Missing = new(LockReadStatus.Missing, null, null);
    }
}

// The one wire shape of watchd.lock, and the only reader of it: the holder writes it, FleetWatchdProbe and
// WatchdDaemonProbe read it, and the three fields are the whole contract — a pid, the host it was claimed on,
// and when that process started. StartedAtUtc is nullable because the holder may not be able to read its own
// start time: absence is an unanswered question, never a start time of now.
public sealed record WatchdLockOwner(int Pid, string? Host, DateTime? StartedAtUtc)
{
    // A reader's two ways of not-knowing are different facts. NotRecorded is a claim that answered nothing;
    // LiveUnreadable is a claim that answered, with a process this machine cannot inspect any more. They are
    // kept apart so each surface can choose its own silence, and neither one is ever read as a drift.
    public enum StartCheck
    {
        NotRecorded,
        LiveUnreadable,
        Agreed,
        Drifted,
    }

    // The clock a daemon writes and the process table answer it are two different reads of one instant, so a
    // claim inside this window is the same process, not a recycled pid.
    public static TimeSpan StartTimeTolerance { get; } = TimeSpan.FromSeconds(2);

    // The two answers a reader asks of a claim are read, never written: the lock file carries pid, host and
    // start time, and the byte shape of those three fields is the compatibility promise to every older claim.
    [JsonIgnore]
    public bool HeldOnThisHost =>
        string.Equals(Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool ClaimedByCurrentProcess => Pid == Environment.ProcessId && HeldOnThisHost;

    public StartCheck CheckAgainstLiveStart(DateTime? liveStartUtc) =>
        StartedAtUtc is not { } recorded
            ? StartCheck.NotRecorded
            : liveStartUtc is not { } started
                ? StartCheck.LiveUnreadable
                : (started - recorded).Duration() <= StartTimeTolerance
                    ? StartCheck.Agreed
                    : StartCheck.Drifted;

    public static DateTime? ReadLiveStartUtc(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    public static WatchdLockOwner? TryRead(string lockPath)
    {
        try
        {
            return JsonSerializer.Deserialize<WatchdLockOwner>(File.ReadAllText(lockPath), SqueezrJson.FileOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

