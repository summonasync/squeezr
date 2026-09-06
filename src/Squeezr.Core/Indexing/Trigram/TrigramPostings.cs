namespace Squeezr.Core.Indexing.Trigram;

public sealed class TrigramPostings
{
    public required uint[] DocIds { get; init; }

    public required int[][] Positions { get; init; }

    public int DocumentCount => DocIds.Length;
}
