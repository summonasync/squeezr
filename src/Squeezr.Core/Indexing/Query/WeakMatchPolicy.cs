namespace Squeezr.Core.Indexing.Query;

public static class WeakMatchPolicy
{
    // the model key the shipped default config resolves to, and the only key with a measured floor
    public const string CalibratedModelKey = "ollama/nomic-embed-text/768/nomic-embed-text-v1.5";

    // model-anchored absolute: nomic-embed-text-v1.5 cosine packs irrelevant queries at ~0.60-0.70
    // and on-topic ones at ~0.74+ (measured live), so a top hit under this line is the model
    // reporting "nothing relevant here" — every other input to the decision is relative
    // private by design: the number means nothing outside its key, and the table is the only reader
    private const double SemanticFloor = 0.72;

    private static readonly IReadOnlyDictionary<string, double> SemanticFloors =
        new Dictionary<string, double>(StringComparer.Ordinal) { [CalibratedModelKey] = SemanticFloor };

    public static IReadOnlyDictionary<string, double> CalibratedFloors => SemanticFloors;

    public static bool HasCalibratedFloor(string? modelKey) =>
        modelKey is not null && SemanticFloors.ContainsKey(modelKey);

    public static bool IsWeak(int exactArmHits, IReadOnlyList<double> semanticScores, string? modelKey) =>
        exactArmHits == 0
        && semanticScores.Count > 0
        && modelKey is not null
        && SemanticFloors.TryGetValue(modelKey, out var floor)
        && semanticScores.Max() < floor;

    public static string CalibrationHint(string modelKey) =>
        $"weak-match floor uncalibrated for {modelKey} — no low-confidence claim made until a floor is measured for it";

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
