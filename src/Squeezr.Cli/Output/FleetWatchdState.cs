using System.Text.Json.Serialization;

namespace Squeezr.Cli.Output;

// What this machine can honestly say about the supervisor process: the lock file names a live
// owner or it does not. A foreign-host owner counts as running — the CLI cannot check a pid in
// another namespace, and claiming absence would be the louder lie.
public sealed record FleetWatchdState(
    bool Running,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Pid);
