using System.Text.Json.Serialization;

namespace Squeezr.Cli.Output;

public sealed record FleetEntry(
    string Name,
    string RootPath,
    string StoreKey,
    FleetDaemonState Watch,
    FleetDaemonState ServeWatch,
    // The per-pid roster: every process the store's beats name, with the claim owner flagged. `watch` and
    // `serveWatch` above keep answering the per-kind question they always answered, so a consumer written
    // against the single-daemon shape reads the same values it always read and this array is purely extra.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<FleetDaemonProcess> Daemons,
    bool Stale,
    // Contract fields must stay present in --json even when MetaStore is unavailable.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? PendingChunks,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTime? LastIndexedAt,
    bool? NameCollision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] FleetWatchAdoption? Adoption = null);
