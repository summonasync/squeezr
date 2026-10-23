using System.Text.Json.Serialization;
using Squeezr.Core.Indexing.Watch;
using Squeezr.Core.Infra;

namespace Squeezr.Cli.Mcp.Onboarding;

// One store's watch standing as the doctor names it: who owns the loop, how many live processes merely observe,
// and the exact command that starts a watcher where none runs. Registrations stopped stamping --watch (070/T5),
// so re-registering a client trades a watcher per session for no watcher at all unless someone starts the loop
// on purpose — this row is what makes that trade checkable instead of silent.
public sealed record StoreWatchDiagnostics(
    string Name,
    string StoreKey,
    string OwnerState,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? OwnerPid,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? OwnerKind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? OwnerHost,
    int Observers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? StartCommand)
{
    public static StoreWatchDiagnostics? From(WatchStoreHealth? health, string name, string storeKey) =>
        health switch
        {
            null => null,
            _ => new(
                name,
                storeKey,
                StateName(health.State),
                health.OwnerPid,
                health.OwnerKind,
                health.OwnerHost,
                health.Observers,
                health.IndexerRunning ? null : RestartCommand(storeKey)),
        };

    // The fleet restart door opens a watcher on a named store from anywhere; a key that is not a plain token
    // gets shell-quoted rather than pasted raw.
    private static string RestartCommand(string storeKey) =>
        storeKey.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? $"squeezr fleet restart {storeKey}"
            : $"squeezr fleet restart {ShellPath.Quote(storeKey)}";

    private static string StateName(WatchIndexerState state) => state switch
    {
        WatchIndexerState.Alive => "alive",
        WatchIndexerState.Wedged => "wedged",
        WatchIndexerState.Gone => "gone",
        WatchIndexerState.Foreign => "foreign",
        _ => "none",
    };
}
