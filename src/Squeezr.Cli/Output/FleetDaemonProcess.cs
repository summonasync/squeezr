using System.Text.Json.Serialization;

namespace Squeezr.Cli.Output;

// One live process on one store, as the beat carrier names it. `owner` is not a second claim decision: it is the
// answer to "does this beat belong to the process watch.owner names", computed once per store and carried down.
public sealed record FleetDaemonProcess(
    string Kind,
    int Pid,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? UptimeSeconds,
    bool Owner,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Host)
{
    public bool IsAlive => Status == FleetDaemonState.AliveStatus;
}
