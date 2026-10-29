namespace Squeezr.Core.Indexing.Watch.Supervision;

public sealed class CrashLoopGuard
{
    public const int RestartLimit = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, List<DateTime>> _restarts = [];
    private readonly HashSet<string> _wedged = [];

    public bool IsWedged(string storeKey) => _wedged.Contains(storeKey);

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
        return false;
    }

    public void Reset(string storeKey)
    {
        _wedged.Remove(storeKey);
        _restarts.Remove(storeKey);
    }
}
