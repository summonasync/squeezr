using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

// The database half of a store copy, on the wire. Fields are mapped one by one off DatabaseCopyStats so a
// Core rename cannot become a JSON break (035 additive discipline), and every number keeps the place it was
// measured in: source, staged copy, installed file.
public sealed record WorktreeDatabaseCopyReport(
    long SourceBytes,
    long CopiedBytes,
    long TargetBytes,
    int SourceFiles,
    int CopiedFiles,
    int TargetFiles,
    int SourceChunks,
    int CopiedChunks,
    int TargetChunks)
{
    public static WorktreeDatabaseCopyReport From(DatabaseCopyStats stats) => new(
        SourceBytes: stats.SourceBytes,
        CopiedBytes: stats.CopiedBytes,
        TargetBytes: stats.TargetBytes,
        SourceFiles: stats.SourceFiles,
        CopiedFiles: stats.CopiedFiles,
        TargetFiles: stats.TargetFiles,
        SourceChunks: stats.SourceChunks,
        CopiedChunks: stats.CopiedChunks,
        TargetChunks: stats.TargetChunks);
}
