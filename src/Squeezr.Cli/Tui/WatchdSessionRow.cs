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
    int? OwnerPid);
