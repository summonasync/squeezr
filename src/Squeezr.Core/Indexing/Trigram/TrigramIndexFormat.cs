namespace Squeezr.Core.Indexing.Trigram;

public static class TrigramIndexFormat
{
    public const uint Magic = 0x53514A52u;
    public const uint CurrentVersion = 2;
    public const int HeaderSize = 36;

    public const int DocEntrySize = 8;
    public const int IndexEntrySize = 12;
    public const int PostingBlockSize = 128;
}
