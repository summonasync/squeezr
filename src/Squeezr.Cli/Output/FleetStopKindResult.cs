using System.Text.Json.Serialization;

namespace Squeezr.Cli.Output;

public sealed record FleetStopKindResult(
    string Kind,
    string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Pid,
    string? Detail);
