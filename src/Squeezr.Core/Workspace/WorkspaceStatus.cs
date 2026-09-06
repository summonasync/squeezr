namespace Squeezr.Core.Workspace;

public sealed record WorkspaceStatus(string Name, IReadOnlyList<RepoStatus> Repos);
