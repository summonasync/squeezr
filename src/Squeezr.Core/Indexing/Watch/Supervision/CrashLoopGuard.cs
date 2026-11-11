namespace Squeezr.Core.Indexing.Watch.Supervision;

public sealed class CrashLoopGuard
{
    public const int RestartLimit = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly SupervisionState _state;
    private readonly Dictionary<string, List<DateTime>> _restarts = [];
    private readonly HashSet<string> _wedged = new(StringComparer.Ordinal);

    public CrashLoopGuard() : this(new SupervisionState())
    {
    }

    internal CrashLoopGuard(SupervisionState state) => _state = state;

    public bool IsWedged(string storeKey) => _wedged.Contains(storeKey);

    // The wedge marker lives in state.json, so the file is the authority and this set is the live
    // process's cache of it: keys the file no longer carries are released with a fresh restart budget,
    // keys another process added block here without waiting for this process to count them itself.
    public void SyncWedged(IReadOnlySet<string> wedgedInState)
    {
        foreach (var storeKey in _wedged.Where(key => !wedgedInState.Contains(key)).ToList())
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
        _state.SetWedged(storeKey, true);
        return false;
    }

    public void Reset(string storeKey)
    {
        _wedged.Remove(storeKey);
        _restarts.Remove(storeKey);
        _state.SetWedged(storeKey, false);
    }
}
