using System.Text.Json.Serialization;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

public sealed record FleetWatchAdoption(
    int AdopterPid,
    string AdopterHost,
    DateTime AdoptedAt,
    int DisplacedPid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DisplacedHost = null)
{
    public static FleetWatchAdoption? FromStore(string workspaceRoot) =>
        WatchAdoptionEventFile.TryRead(SqueezrPaths.StoreDir(workspaceRoot)) is { } adopted
            ? new FleetWatchAdoption(
                adopted.AdopterPid,
                adopted.AdopterHost,
                adopted.AdoptedAt,
                adopted.DisplacedPid,
                adopted.DisplacedHost)
            : null;
}
