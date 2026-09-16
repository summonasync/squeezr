using System.Text.Json.Serialization;

namespace Squeezr.Cli.Output;

public sealed record FleetRestartResult(
    string Name,
    string StoreKey,
    string PreviousStatus,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? PreviousPid,
    int Pid);
