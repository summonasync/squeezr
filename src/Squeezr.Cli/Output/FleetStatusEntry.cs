using System.Text.Json.Serialization;
using Squeezr.Core.Indexing.Watch;

namespace Squeezr.Cli.Output;

public sealed record FleetStatusEntry(
    string Name,
    string StoreKey,
    string Desired,
    string Supervision,
    string IndexerState,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? OwnerPid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? OwnerKind,
    // The store's own durable pass record: a running, stuck or interrupted pass names its phase and
    // its last progress. A clean completion clears the record, and null here says exactly that —
    // the cell never invents a pass time the store does not carry.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PassPhaseStamp? LastPass,
    bool Stale);
