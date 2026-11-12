using Squeezr.Core.Indexing.Watch.Supervision;

namespace Squeezr.Cli.Tui;

public sealed record WatchdSessionRow(
    string StoreKey,
    string Name,
    string RootPath,
    WatchDesire Desire,
    WatchdSessionState State,
    string Health,
    string LastPass,
    int? OwnerPid)
{
    // DesireKnown is false when state.json exists but cannot be read: the row's desire would then be the
    // running default standing in for an unreadable wish, and no surface here may present that as a wish.
    public bool DesireKnown { get; init; } = true;

    // RootResolves is false when this machine cannot find the store the registry row names. A desire written
    // for a root that is not here is a record nobody will ever carry out, so the row says it cannot receive one.
    public bool RootResolves { get; init; } = true;
}
