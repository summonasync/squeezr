namespace Squeezr.Core.Workspace;

public sealed record WorkspaceManifest
{
    public required string Name { get; init; }

    public IReadOnlyList<RepoEntry> Repos { get; init; } = [];
}
