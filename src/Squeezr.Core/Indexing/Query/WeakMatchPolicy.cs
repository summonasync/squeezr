namespace Squeezr.Core.Indexing.Query;

public static class WeakMatchPolicy
{
    // model-anchored absolute: nomic-embed-text-v1.5 cosine packs irrelevant queries at ~0.60-0.70
    // and on-topic ones at ~0.74+ (measured live), so a top hit under this line is the model
    // reporting "nothing relevant here" — every other input to the decision is relative
    public const double SemanticFloor = 0.72;

    public static bool IsWeak(int exactArmHits, IReadOnlyList<double> semanticScores) =>
        exactArmHits == 0
        && semanticScores.Count > 0
        && semanticScores.Max() < SemanticFloor;

    public static string Warning(int deliveredSemanticHits, string? kind)
    {
        var noun = deliveredSemanticHits == 1 ? "hit" : "hits";
        var moves = new List<string>();
        if (DocKindDetector.FilterKind(kind) is null)
        {
            moves.Add($"kind={DocKindDetector.Code}");
        }

        moves.Add("prefix");

        var offered = moves.Count == 1
            ? moves[0]
            : $"{string.Join(", ", moves.Take(moves.Count - 1))} or {moves[^1]}";
        return $"no exact-arm match; {deliveredSemanticHits} semantic {noun} at low confidence — narrow with {offered}";
    }
}
