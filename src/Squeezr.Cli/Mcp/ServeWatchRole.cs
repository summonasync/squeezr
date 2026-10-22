using System.Text.Json;
using System.Text.Json.Serialization;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Serialization;

namespace Squeezr.Cli.Mcp;

// One claim outcome, three renderings. Everything said about who owns the store is built from the outcome the
// engine gate itself obeyed, so the notice cannot drift from the decision while the decision stays right. A pid
// is named only where the outcome carries one: a foreign-host pid lives in another machine's namespace and is
// reported by host, and the reclaim-blocked verdicts have no owner to blame at all.
internal sealed record ServeWatchRole(
    bool IsOwner,
    WatchOwnerStatus Verdict,
    int? OwnerPid,
    string? OwnerHost)
{
    public static ServeWatchRole FromOutcome(WatchOwnerOutcome outcome) =>
        new(outcome.IsOwner, outcome.Status, outcome.Owner?.Pid, outcome.Owner?.Host);

    public string Role => IsOwner ? "owner" : "observer";

    public string MachineLine => JsonSerializer.Serialize(
        new WatchRoleReport("watchRole", Role, Verdict.ToString(), OwnerPid), SqueezrJson.OutputOptions);

    public string StartupLine =>
        $"watch not started: {Reason}; serving queries + per-query freshness";

    public string AgentNotice =>
        $"watch engine off on this session: {Reason}; this session answers queries and refreshes only what a " +
        "query touches, it runs no indexing pass";

    private string Reason => Verdict switch
    {
        WatchOwnerStatus.OwnerWedged when OwnerPid is { } wedged =>
            $"pid {wedged} holds this store's claim with a stale beat under a live process",
        WatchOwnerStatus.ForeignOwnerHold when OwnerHost is { } host =>
            $"host '{host}' holds this store's claim and the foreign takeover window has not passed",
        WatchOwnerStatus.MarkerUnreadable => "the owner marker is unreadable and not yet reclaimable",
        WatchOwnerStatus.RaceLost when OwnerPid is { } winner =>
            $"pid {winner} won the claim race for this store",
        WatchOwnerStatus.RaceLost => "another process won the claim race for this store",
        WatchOwnerStatus.StoreUnavailable => "the store directory could not be written",
        _ when OwnerPid is { } owner => $"pid {owner} owns this store",
        _ => "another process owns this store"
    };

    private sealed record WatchRoleReport(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("verdict")] string Verdict,
        [property: JsonPropertyName("ownerPid")] int? OwnerPid);
}
