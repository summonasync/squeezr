namespace Squeezr.Cli.Output;

public sealed record WorkspaceListEntry(
    string Name,
    string RootPath,
    string StoreKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt,
    bool Stale,
    bool? Husk = null,
    bool? NameCollision = null);
