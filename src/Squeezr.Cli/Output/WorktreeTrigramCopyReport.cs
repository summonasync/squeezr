using Squeezr.Core.Storage;

namespace Squeezr.Cli.Output;

// The trigram half of a store copy, on the wire. Copied=false with zeros is the state where the main
// worktree never wrote an index — reported as an absent file, never as an empty success.
public sealed record WorktreeTrigramCopyReport(
    bool Copied,
    long SourceBytes,
    long CopiedBytes,
    long TargetBytes,
    int SourceDocs,
    int CopiedDocs,
    int TargetDocs)
{
    public static WorktreeTrigramCopyReport From(TrigramCopyStats stats) => new(
        Copied: stats.Copied,
        SourceBytes: stats.SourceBytes,
        CopiedBytes: stats.CopiedBytes,
        TargetBytes: stats.TargetBytes,
        SourceDocs: stats.SourceDocs,
        CopiedDocs: stats.CopiedDocs,
        TargetDocs: stats.TargetDocs);
}
