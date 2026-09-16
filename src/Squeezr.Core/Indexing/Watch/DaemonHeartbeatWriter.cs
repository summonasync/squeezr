using System.Text.Json;
using Squeezr.Core.Serialization;

namespace Squeezr.Core.Indexing.Watch;

public sealed class DaemonHeartbeatWriter : IDisposable
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _kind;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _beatInterval;
    private readonly Action<string>? _onWarning;
    private readonly int _pid;
    private readonly Timer _timer;
    private DateTime _startedAt;
    private bool _disposed;

    public DaemonHeartbeatWriter(
        string storeDir,
        string kind = DaemonHeartbeat.DefaultKind,
        Func<DateTime>? utcNow = null,
        TimeSpan? beatInterval = null,
        Action<string>? onWarning = null)
    {
        _filePath = DaemonHeartbeat.PathFor(storeDir, kind);
        _kind = kind;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _beatInterval = beatInterval ?? DaemonHeartbeat.BeatInterval;
        _onWarning = onWarning;
        _pid = Environment.ProcessId;
        _timer = new Timer(_ => Beat(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public string FilePath => _filePath;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _timer.Change(_beatInterval, _beatInterval);
        }
        Beat();
    }

    public void Beat()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var tempPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                if (_startedAt == default)
                {
                    _startedAt = _utcNow();
                }
                var heartbeat = new DaemonHeartbeat(_pid, _kind, _startedAt, _utcNow());
                File.WriteAllText(tempPath, JsonSerializer.Serialize(heartbeat, SqueezrJson.FileOptions));
                File.Move(tempPath, _filePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(tempPath);
                _onWarning?.Invoke($"heartbeat write failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _timer.Dispose();
        }
        TryDelete(_filePath);
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
