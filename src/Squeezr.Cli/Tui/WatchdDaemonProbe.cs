using System.Text.Json;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Indexing.Watch.Supervision;
using Squeezr.Core.Serialization;

namespace Squeezr.Cli.Tui;

public sealed class WatchdDaemonProbe
{
    private readonly string _lockPath;
    private readonly Func<int, bool> _isProcessAlive;

    public WatchdDaemonProbe() : this(WatchdPaths.LockPath)
    {
    }

    internal WatchdDaemonProbe(string lockPath) : this(lockPath, DaemonHeartbeatReader.IsProcessAlive)
    {
    }

    internal WatchdDaemonProbe(string lockPath, Func<int, bool> isProcessAlive)
    {
        _lockPath = lockPath;
        _isProcessAlive = isProcessAlive;
    }

    public WatchdDaemonStatus Inspect()
    {
        var owner = TryReadOwner();
        if (owner is null)
        {
            return WatchdDaemonStatus.NotRunning;
        }

        if (!string.Equals(owner.Host, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            return new WatchdDaemonStatus(true, owner.Pid);
        }

        return new WatchdDaemonStatus(_isProcessAlive(owner.Pid), owner.Pid);
    }

    private LockOwner? TryReadOwner()
    {
        string content;
        try
        {
            content = File.ReadAllText(_lockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LockOwner>(content, SqueezrJson.FileOptions) is { Pid: > 0 } owner
                ? owner
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record LockOwner(int Pid, string? Host);
}
