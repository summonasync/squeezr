namespace Squeezr.Core.Indexing.Watch.Supervision;

public sealed class CrashLoopGuard
{
    public const int RestartLimit = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly SupervisionState _state;
    private readonly Dictionary<string, List<DateTime>> _restarts = [];
    private readonly HashSet<string> _wedged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _unflushedWedges = new(StringComparer.Ordinal);

    public CrashLoopGuard() : this(new SupervisionState())
    {
    }

    internal CrashLoopGuard(SupervisionState state) => _state = state;

    public bool IsWedged(string storeKey) => _wedged.Contains(storeKey);

    public string? WedgeWriteFault(string storeKey) => _unflushedWedges.GetValueOrDefault(storeKey);

    // The wedge marker lives in state.json, so the file is the authority and this set is the live
    // process's cache of it: keys the file no longer carries are released with a fresh restart budget,
    // keys another process added block here without waiting for this process to count them itself.
    // A marker this process failed to write is the one exception — the file says nothing about it while
    // the store is crashing, so memory holds it until the write lands instead of handing out a fresh
    // budget on every sweep and turning the wedge into a restart flap nobody sees.
    public void SyncWedged(IReadOnlySet<string> wedgedInState)
    {
        foreach (var storeKey in _wedged
                     .Where(key => !wedgedInState.Contains(key) && !_unflushedWedges.ContainsKey(key))
                     .ToList())
        {
            _wedged.Remove(storeKey);
            _restarts.Remove(storeKey);
        }

        foreach (var storeKey in wedgedInState)
        {
            if (_wedged.Add(storeKey))
            {
                _restarts.Remove(storeKey);
            }
        }

        foreach (var storeKey in _unflushedWedges.Keys.ToList())
        {
            FlushWedge(storeKey);
        }
    }

    public bool RegisterRestart(string storeKey, DateTime utcNow)
    {
        if (_wedged.Contains(storeKey))
        {
            return false;
        }

        if (!_restarts.TryGetValue(storeKey, out var window))
        {
            window = [];
            _restarts[storeKey] = window;
        }

        window.RemoveAll(stamp => utcNow - stamp > Window);
        window.Add(utcNow);
        if (window.Count <= RestartLimit)
        {
            return true;
        }

        _wedged.Add(storeKey);
        FlushWedge(storeKey);
        return false;
    }

    public void Reset(string storeKey)
    {
        _wedged.Remove(storeKey);
        _restarts.Remove(storeKey);
        _unflushedWedges.Remove(storeKey);
        _state.SetWedged(storeKey, false);
    }

    private void FlushWedge(string storeKey)
    {
        try
        {
            _state.SetWedged(storeKey, true);
            _unflushedWedges.Remove(storeKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _unflushedWedges[storeKey] = ex.Message;
        }
    }
}
