using System.Diagnostics;
using System.Text.Json;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Core.Indexing.Watch.Supervision;

public sealed class WatchdLock : IDisposable
{
    private static readonly TimeSpan StartedAtTolerance = TimeSpan.FromSeconds(2);

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
        _processStartUtc = processStartUtc ?? ReadProcessStartUtc;
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
            _processStartUtc(Environment.ProcessId) ?? DateTime.UtcNow);
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
        if (ReadLock().Owner is { } owner && owner.IsOwnedByCurrentProcess())
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
        if (!string.Equals(owner.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!_isProcessAlive(owner.Pid))
        {
            return false;
        }

        return _processStartUtc(owner.Pid) is not { } started
            || (started - owner.StartedAtUtc).Duration() <= StartedAtTolerance;
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

    private static DateTime? ReadProcessStartUtc(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
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

    private sealed record WatchdLockOwner(int Pid, string Host, DateTime StartedAtUtc)
    {
        public bool IsOwnedByCurrentProcess() =>
            Pid == Environment.ProcessId
            && string.Equals(Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }
}
