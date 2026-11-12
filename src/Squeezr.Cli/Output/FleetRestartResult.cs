using System.Text.Json.Serialization;

namespace Squeezr.Cli.Output;

public sealed record FleetRestartResult(
    string Name,
    string StoreKey,
    string PreviousStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? PreviousPid,
    int Pid,
    // Additive since 108/T2: a restart re-arms the store — desired running, crash-loop wedge cleared.
    string Desired = "running",
    // Additive since 113/T2: false when the daemon this command spawned was already gone at report time,
    // so the pid in the payload is never read as a running watcher. Defaulted for older readers.
    bool WatcherAlive = true);
