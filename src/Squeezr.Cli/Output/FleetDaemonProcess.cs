using System.Text.Json.Serialization;

namespace Squeezr.Cli.Output;

// One live process on one store, as the beat carrier names it. `owner` is not a second claim decision: it is the
// answer to "does this beat belong to the process watch.owner names", computed once per store and carried down.
// The activity tail (phase through executor) is additive per 035 and repeats what the process's own beat already
// published; a row that is not alive publishes none of it, because a beat that aged out is not proof of work.
// cpuPercent/rssBytes are fleet's own measurement of the pids the roster proved alive — null means not measured.
public sealed record FleetDaemonProcess(
    string Kind,
    int Pid,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? UptimeSeconds,
    bool Owner,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Host,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Phase = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Repo = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DoneItems = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? TotalItems = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? PhaseSeconds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Provider = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Executor = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? CpuPercent = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? RssBytes = null)
{
    public bool IsAlive => Status == FleetDaemonState.AliveStatus;
}
