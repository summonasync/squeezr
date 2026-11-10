namespace Squeezr.Core.Storage;

// The trigram half of the three-number rule. A source store that never wrote an index has nothing to
// inherit, and that is reported as Copied=false with zeros rather than as an empty success.
public sealed record TrigramCopyStats(
    bool Copied,
    long SourceBytes,
    long CopiedBytes,
    long TargetBytes,
    int SourceDocs,
    int CopiedDocs,
    int TargetDocs)
{
    public static TrigramCopyStats NotCopied { get; } = new(false, 0, 0, 0, 0, 0, 0);
}
