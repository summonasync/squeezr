using System.Text.Json.Serialization;

namespace Squeezr.Cli.Output;

public sealed record FleetEntry(
    string Name,
    string RootPath,
    string StoreKey,
    FleetDaemonState Watch,
    FleetDaemonState ServeWatch,
    bool Stale,
    // Contract fields must stay present in --json even when MetaStore is unavailable.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? PendingChunks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? LastIndexedAt,
    bool? NameCollision = null);
