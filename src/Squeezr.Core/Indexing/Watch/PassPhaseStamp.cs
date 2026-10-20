namespace Squeezr.Core.Indexing.Watch;

public sealed record PassPhaseStamp(
    int Pid,
    string Phase,
    DateTime StartedAt,
    DateTime LastProgressAt,
    int BatchSize)
{
    public const string RunningPhase = "running";
    public const string StuckPhase = "stuck";

    // Written in place of erasing the record when a stop request cut a running pass short. The batch it had
    // already dequeued dies with the process, so this record is the durable hand-off that tells the next
    // start "work was left unfinished" and arms its reconciliation pass. Naming it apart from running/stuck
    // is what keeps the two rules from colliding: a running or stuck record belongs to a writer that may
    // still be alive and must never be touched, while this one is always the newcomer's to consume.
    public const string InterruptedPhase = "interrupted";
    public const string FileName = "pass.phase";

    public static string PathFor(string storeDir) => Path.Combine(storeDir, FileName);
}
