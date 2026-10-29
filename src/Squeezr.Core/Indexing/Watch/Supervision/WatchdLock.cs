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
        if (TryReadOwner() is { } owner && IsOwnerAlive(owner))
        {
            return new WatchdLockOutcome(false, owner.Pid);
        }

        TryStealStaleLock();
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
            return new WatchdLockOutcome(false, TryReadOwner()?.Pid);
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
        if (TryReadOwner() is { } owner && owner.IsOwnedByCurrentProcess())
        {
            TryDelete();
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

    private void TryStealStaleLock()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private WatchdLockOwner? TryReadOwner()
    {
        try
        {
            return JsonSerializer.Deserialize<WatchdLockOwner>(File.ReadAllText(_path), SqueezrJson.FileOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
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

    private sealed record WatchdLockOwner(int Pid, string Host, DateTime StartedAtUtc)
    {
        public bool IsOwnedByCurrentProcess() =>
            Pid == Environment.ProcessId
            && string.Equals(Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }
}
