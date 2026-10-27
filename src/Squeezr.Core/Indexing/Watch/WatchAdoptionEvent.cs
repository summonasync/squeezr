namespace Squeezr.Core.Indexing.Watch;

/// <summary>
/// One takeover of a watch claim whose owner died, recorded on the store the moment the claim moved.
/// Deferred embeddings stall exactly here when nobody adopts, so the event is the honest answer to who
/// unblocked the queue and when; fleet and status read it, nothing rewrites it.
/// </summary>
public sealed record WatchAdoptionEvent(
    int AdopterPid,
    string AdopterHost,
    string AdopterKind,
    DateTime AdoptedAt,
    int DisplacedPid,
    string DisplacedHost,
    DateTime DisplacedStartedAt,
    DateTime DisplacedLastBeatAt)
{
    public const string FileName = "watch.adoption";

    public static string PathFor(string storeDir) => Path.Combine(storeDir, FileName);
}
