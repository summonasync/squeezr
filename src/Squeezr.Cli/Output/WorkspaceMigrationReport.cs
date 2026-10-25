namespace Squeezr.Cli.Output;

public sealed record WorkspaceMigrationReport(
    string SourceDir,
    string DestinationDir,
    string StoreKey,
    int MovedFileCount);
