namespace Squeezr.Core.Workspace;

public sealed record WorkspaceManifest
{
    public required string Name { get; init; }

    public string? RootPath { get; init; }

    public IReadOnlyList<RepoEntry> Repos { get; init; } = [];

    public ReconcileMarker? Reconcile { get; init; }
}
