namespace Squeezr.Core.Storage;

public sealed record WorkspaceMigrationResult(
    string SourceDir,
    string StoreDir,
    string StoreKey,
    int MovedFileCount);
