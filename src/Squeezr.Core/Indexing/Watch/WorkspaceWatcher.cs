using Squeezr.Core.Configuration;
using Squeezr.Core.Workspace;

namespace Squeezr.Core.Indexing.Watch;

public interface IWatchStatus
{
    void PassStarted(int changedPaths);

    void PassCompleted(IndexResult result);

    void RetryScheduled(TimeSpan delay);

    void Faulted(string error);
}

public sealed class WorkspaceWatcher
{
    private const int EventBufferBytes = 64 * 1024;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);

    private readonly WorkspaceIndexer _indexer = new();
    private readonly IWatchStatus? _status;
    private readonly IIndexProgress? _progress;
    private readonly Action<string>? _onStaleTakeover;
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = [];

    private List<FileSystemWatcher> _watchers = [];
    private WatchEventFilter? _filter;
    private CancellationTokenSource? _stopping;
    private Task _runTask = Task.CompletedTask;
    private string _workspaceRoot = string.Empty;
    private TimeSpan _debounceWindow;
    private TimeSpan _backoffCap;
    private DateTime _lastEventAt = DateTime.MinValue;
    private bool _forcePass;
    private bool _running;

    public WorkspaceWatcher(
        IWatchStatus? status = null,
        IIndexProgress? progress = null,
        Action<string>? onStaleTakeover = null)
    {
        _status = status;
        _progress = progress;
        _onStaleTakeover = onStaleTakeover;
    }

    public Task StartAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        if (_running)
        {
            throw new InvalidOperationException("Watcher is already running.");
        }
        if (!WorkspaceManifestFile.Exists(workspaceRoot))
        {
            throw new WorkspaceException(
                "Workspace is not initialized. Run 'squeezr init' first.",
                WorkspaceException.NotInitializedCode);
        }

        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        var config = LoadConfig(_workspaceRoot);
        _debounceWindow = TimeSpan.FromSeconds(config.Watch.DebounceSeconds);
        _backoffCap = TimeSpan.FromSeconds(config.Watch.BackoffMaxSeconds);
        _filter = new WatchEventFilter(
            _workspaceRoot, ResolveRepoRoots(_workspaceRoot), config.Indexing.Ignore);

        lock (_gate)
        {
            _pending.Clear();
            _forcePass = false;
            _lastEventAt = DateTime.MinValue;
        }

        _watchers = CreateWatchers();
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running = true;
        _runTask = RunAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        var stopping = Interlocked.Exchange(ref _stopping, null);
        if (stopping is null)
        {
            return;
        }

        stopping.Cancel();
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            stopping.Dispose();
        }
    }

    private static SqueezrConfig LoadConfig(string workspaceRoot)
    {
        try
        {
            return ConfigFile.Load(workspaceRoot);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or WorkspaceException)
        {
            return SqueezrConfig.CreateDefault();
        }
    }

    private static IReadOnlyList<string> ResolveRepoRoots(string workspaceRoot)
    {
        var manifest = WorkspaceManifestFile.Load(workspaceRoot);
        return manifest.Repos
            .Select(repo => Path.GetFullPath(Path.IsPathRooted(repo.Path) ? repo.Path : Path.Combine(workspaceRoot, repo.Path)))
            .Where(Directory.Exists)
            .ToList();
    }

    private List<FileSystemWatcher> CreateWatchers()
    {
        var watchers = new List<FileSystemWatcher>();
        try
        {
            foreach (var root in _filter!.RepoRoots)
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = EventBufferBytes,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                watcher.Created += (_, e) => OnEvent(e.FullPath, pathMayBeGone: false);
                watcher.Changed += (_, e) => OnEvent(e.FullPath, pathMayBeGone: false);
                watcher.Deleted += (_, e) => OnEvent(e.FullPath, pathMayBeGone: true);
                watcher.Renamed += (_, e) =>
                {
                    OnEvent(e.OldFullPath, pathMayBeGone: true);
                    OnEvent(e.FullPath, pathMayBeGone: false);
                };
                watcher.Error += OnWatcherError;
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
        }
        catch
        {
            foreach (var watcher in watchers)
            {
                watcher.Dispose();
            }
            throw;
        }
        return watchers;
    }

    private void OnEvent(string fullPath, bool pathMayBeGone)
    {
        if (!_filter!.ShouldTrack(fullPath, pathMayBeGone))
        {
            return;
        }

        lock (_gate)
        {
            _pending.Add(fullPath);
            _lastEventAt = DateTime.UtcNow;
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _status?.Faulted($"file system watcher dropped events: {e.GetException().Message}");
        lock (_gate)
        {
            _forcePass = true;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var batch = CollectDueBatch();
                if (batch is null)
                {
                    await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await RunPassAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }
        }
    }

    private List<string>? CollectDueBatch()
    {
        lock (_gate)
        {
            if (_pending.Count == 0 && !_forcePass)
            {
                return null;
            }

            if (!_forcePass && DateTime.UtcNow - _lastEventAt < _debounceWindow)
            {
                return null;
            }

            var batch = _pending.ToList();
            _pending.Clear();
            _forcePass = false;
            return batch;
        }
    }

    private async Task RunPassAsync(List<string> batch, CancellationToken cancellationToken)
    {
        _status?.PassStarted(batch.Count);
        var backoff = InitialBackoff;
        while (true)
        {
            try
            {
                var result = _indexer.Index(_workspaceRoot, progress: _progress, onStaleTakeover: _onStaleTakeover);
                _status?.PassCompleted(result);
                return;
            }
            catch (WorkspaceLockedException)
            {
                _status?.RetryScheduled(backoff);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException
                and not StackOverflowException
                and not OperationCanceledException)
            {
                _status?.Faulted($"index pass failed: {ex.Message}");
                lock (_gate)
                {
                    foreach (var path in batch)
                    {
                        _pending.Add(path);
                    }
                    _lastEventAt = DateTime.UtcNow;
                }
                return;
            }

            await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _backoffCap.Ticks));
        }
    }
}
