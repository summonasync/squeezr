using System.Text.Json;
using Squeezr.Core.Serialization;

namespace Squeezr.Core.Indexing.Watch;

public sealed class DaemonHeartbeatWriter : IDisposable
{
    // A crashed daemon leaves its beat in the directory behind, and a fleet reading that directory would keep
    // naming a process nobody runs. One pass a minute is enough to collect them: the sweep is garbage work, not
    // a liveness path, and a beat nobody can prove dead yet simply stays.
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly string _storeDir;
    private readonly string _kind;
    private readonly string _kindPath;
    private readonly string _beatPath;
    private bool _publishKindHeartbeat;
    private readonly Func<DateTime> _utcNow;
    private readonly TimeSpan _beatInterval;
    private readonly Action<string>? _onWarning;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly int _pid;
    private readonly string _host;
    private readonly Timer _timer;
    private DateTime _startedAt;
    private DateTime _nextSweepAt;
    private bool _disposed;

    public DaemonHeartbeatWriter(
        string storeDir,
        string kind = DaemonHeartbeat.DefaultKind,
        Func<DateTime>? utcNow = null,
        TimeSpan? beatInterval = null,
        Action<string>? onWarning = null,
        bool publishKindHeartbeat = true,
        string? host = null,
        Func<int, bool>? isProcessAlive = null)
    {
        _storeDir = storeDir;
        _kind = kind;
        _kindPath = DaemonHeartbeat.PathFor(storeDir, kind);
        _pid = Environment.ProcessId;
        _beatPath = DaemonHeartbeat.BeatPathFor(storeDir, kind, _pid);
        _publishKindHeartbeat = publishKindHeartbeat;
        _host = host ?? Environment.MachineName;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _beatInterval = beatInterval ?? DaemonHeartbeat.BeatInterval;
        _onWarning = onWarning;
        _isProcessAlive = isProcessAlive ?? DaemonHeartbeatReader.IsProcessAlive;
        _nextSweepAt = DateTime.MinValue;
        _timer = new Timer(_ => Beat(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public string FilePath => _kindPath;

    public string BeatPath => _beatPath;

    // The demotion door: a writer the store claim has moved away from must stop stamping the per-kind heartbeat
    // the moment the claim is gone, not at its own shutdown — until now this file was the pre-roster reader's
    // only answer to "who runs the watch loop here", and a demoted writer kept signing its own name there.
    // The mirror is deleted only while it still names this process: whoever won the claim has already stamped
    // its own file, and erasing that one would leave old readers with no indexer at all.
    public void StopPublishingKindHeartbeat()
    {
        bool wasPublishing;
        lock (_gate)
        {
            wasPublishing = _publishKindHeartbeat && !_disposed;
            _publishKindHeartbeat = false;
        }

        if (!wasPublishing)
        {
            return;
        }

        var mirror = DaemonHeartbeatReader.TryRead(_storeDir, _kind);
        if (mirror is not null
            && mirror.Pid == _pid
            && (mirror.Host is null || string.Equals(mirror.Host, _host, StringComparison.OrdinalIgnoreCase)))
        {
            TryDelete(_kindPath);
        }
    }

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

            var beatTemp = $"{_beatPath}.{Guid.NewGuid():N}.tmp";
            var kindTemp = $"{_kindPath}.{Guid.NewGuid():N}.tmp";
            try
            {
                var now = _utcNow();
                if (_startedAt == default)
                {
                    _startedAt = now;
                }

                var heartbeat = new DaemonHeartbeat(_pid, _kind, _startedAt, now, _host);
                EnsureBeatsDir();
                Write(beatTemp, _beatPath, heartbeat);
                if (_publishKindHeartbeat)
                {
                    Write(kindTemp, _kindPath, heartbeat);
                }

                if (now >= _nextSweepAt)
                {
                    _nextSweepAt = now + SweepInterval;
                    SweepLeftovers(now);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(beatTemp);
                TryDelete(kindTemp);
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

        TryDelete(_beatPath);
        if (_publishKindHeartbeat)
        {
            TryDelete(_kindPath);
        }
    }

    private void EnsureBeatsDir()
    {
        // The store dir is the caller's precondition, not ours to mint: a daemon whose store was dropped must
        // not resurrect a directory nothing resolves anymore just to keep beating in it.
        if (!Directory.Exists(_storeDir))
        {
            throw new IOException($"store directory '{_storeDir}' is missing");
        }

        Directory.CreateDirectory(DaemonHeartbeat.BeatsDirFor(_storeDir));
    }

    private static void Write(string tempPath, string targetPath, DaemonHeartbeat heartbeat)
    {
        File.WriteAllText(tempPath, JsonSerializer.Serialize(heartbeat, SqueezrJson.FileOptions));
        File.Move(tempPath, targetPath, overwrite: true);
    }

    // Only a beat whose process is gone AND whose last beat has already aged past the stale window is proof of
    // nothing any more. A beat naming another host is that host's business: its pid lives in a namespace this
    // process cannot read, so it is never collected here.
    private void SweepLeftovers(DateTime now)
    {
        string[] candidates;
        try
        {
            candidates = Directory.EnumerateFiles(DaemonHeartbeat.BeatsDirFor(_storeDir)).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var path in candidates)
        {
            if (IsLeftover(path, now))
            {
                TryDelete(path);
            }
        }
    }

    private bool IsLeftover(string path, DateTime now)
    {
        var name = Path.GetFileName(path);
        if (!DaemonHeartbeat.TryParseBeatFileName(name, out _, out var filePid))
        {
            // A write interrupted by a hard kill leaves its temp behind in this directory. Only this carrier's
            // own temp shape is touched, and only once it is old enough that no move can still be pending.
            return name.EndsWith(".tmp", StringComparison.Ordinal)
                && name.Contains(DaemonHeartbeat.BeatExtension, StringComparison.Ordinal)
                && TryReadLastWriteUtc(path) is DateTime written
                && now - written > DaemonHeartbeat.StaleThreshold;
        }

        if (filePid == _pid)
        {
            return false;
        }

        var beat = ReadBeat(path);
        if (beat is null)
        {
            // A beat this reader cannot parse has no process to prove: collect it on file age alone.
            return TryReadLastWriteUtc(path) is DateTime written
                && now - written > DaemonHeartbeat.StaleThreshold;
        }

        if (beat.Host is not null && !string.Equals(beat.Host, _host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return now - beat.LastBeatAt > DaemonHeartbeat.StaleThreshold && !_isProcessAlive(beat.Pid);
    }

    private static DaemonHeartbeat? ReadBeat(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<DaemonHeartbeat>(File.ReadAllText(path), SqueezrJson.FileOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static DateTime? TryReadLastWriteUtc(string path)
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
