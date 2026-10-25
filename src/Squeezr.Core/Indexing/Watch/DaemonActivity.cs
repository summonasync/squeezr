namespace Squeezr.Core.Indexing.Watch;

public sealed record DaemonActivity(
    string Phase,
    string Repo,
    int DoneItems,
    int TotalItems,
    string? CurrentItem,
    DateTime PhaseStartedAt,
    string? Executor = null,
    string? Provider = null);
