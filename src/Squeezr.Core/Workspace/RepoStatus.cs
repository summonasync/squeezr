namespace Squeezr.Core.Workspace;

public sealed record RepoStatus(string Name, string Path, int Files, int Symbols, int Chunks, DateTime? LastIndexedAt);
