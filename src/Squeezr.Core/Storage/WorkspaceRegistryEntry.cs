namespace Squeezr.Core.Storage;

public sealed record WorkspaceRegistryEntry(
    string Name,
    string RootPath,
    string StoreKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUsedAt);
