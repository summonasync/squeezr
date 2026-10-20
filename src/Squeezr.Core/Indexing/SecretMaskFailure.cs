namespace Squeezr.Core.Indexing;

// why a configured mask protects nothing. The reason leaves the guard as a count on the Warnings channel —
// the mask itself never does, the same discipline the withheld tally follows.
internal enum SecretMaskFailure
{
    Negated,
    DirectoryOnly,
    UnsupportedPattern,
}
