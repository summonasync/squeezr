namespace Squeezr.Core.Storage;

// The three numbers spec decision 4 demands for one copied file, each measured where it claims to be:
// source bytes off the file the pass read, copied bytes off the staging temp it verified, target bytes
// off the installed file the worktree will actually open. Counts come from the same two places as the
// bytes they describe, so a run that moved nothing reports zeros instead of a success.
public sealed record DatabaseCopyStats(
    long SourceBytes,
    long CopiedBytes,
    long TargetBytes,
    int SourceFiles,
    int CopiedFiles,
    int TargetFiles,
    int SourceChunks,
    int CopiedChunks,
    int TargetChunks);
