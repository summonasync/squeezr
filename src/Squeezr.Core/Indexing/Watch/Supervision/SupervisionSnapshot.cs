namespace Squeezr.Core.Indexing.Watch.Supervision;

public sealed record SupervisionSnapshot(
    SupervisionStateStatus Status,
    string FilePath,
    IReadOnlyDictionary<string, WatchDesire> Desires,
    IReadOnlySet<string> Wedged,
    string? Fault = null)
{
    public WatchDesire DesireOf(string storeKey) =>
        Desires.TryGetValue(storeKey, out var desire) ? desire : WatchDesire.Running;

    public bool IsWedged(string storeKey) => Wedged.Contains(storeKey);
}
