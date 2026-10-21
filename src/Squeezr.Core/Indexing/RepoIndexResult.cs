namespace Squeezr.Core.Indexing;

// GuardWithheld counts paths this pass refused entry for; GuardWithheldBuiltIns is the half of that the
// built-in masks decide alone, so the configured half is the difference. GuardDropped counts rows already in
// the store that the same decision removed — a file deleted from disk since it was indexed can only leave
// that way. Counts only: a withheld path is never named. Throttled counts csharp files a paced pass held
// out of the parse while the session-retry window was open — deferred work booked into the next pass, not
// skipped work. Additive fields, declared per 035.
public sealed record RepoIndexResult(
    string Name, int Added, int Modified, int Deleted, int Skipped, int Files, int Ignored,
    IReadOnlyDictionary<string, int>? IgnoredBySource = null,
    int GuardWithheld = 0,
    int GuardWithheldBuiltIns = 0,
    int GuardDropped = 0,
    int Embedded = 0,
    bool EmbeddingsCompleted = true,
    int Throttled = 0);
