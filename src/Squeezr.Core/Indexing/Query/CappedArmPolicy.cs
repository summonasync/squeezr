namespace Squeezr.Core.Indexing.Query;

public static class CappedArmPolicy
{
    public static bool IsCapped(SemanticArm arm) =>
        arm.RequestedTopK > ScopedVectorBudget.BaseTopK
        && arm.StoreCandidates >= arm.RequestedTopK
        && arm.Hits.Count < ScopedVectorBudget.BaseTopK;

    public static string? Warning(
        SemanticArm arm,
        int exactArmHits,
        string? repo,
        string? prefix,
        string? kind,
        string? pathGlob)
    {
        if (!IsCapped(arm))
        {
            return null;
        }

        // the complaint needs a victim: either every in-scope candidate was delivered, or the
        // exact arm answered while the semantic arm left the whole scope undelivered
        if (arm.Hits.Count == arm.InScopeCandidates
            || (arm.Hits.Count == 0 && exactArmHits > 0))
        {
            return null;
        }

        var moves = new List<string>();
        if (string.IsNullOrWhiteSpace(pathGlob))
        {
            moves.Add("--path-glob");
        }

        if (DocKindDetector.FilterKind(kind) is null)
        {
            moves.Add($"kind={DocKindDetector.Code}");
        }

        moves.Add(prefix is null ? "prefix" : "narrower prefix");
        if (repo is null)
        {
            moves.Add("repo scope");
        }

        var offered = moves.Count == 1
            ? moves[0]
            : $"{string.Join(", ", moves.Take(moves.Count - 1))} or {moves[^1]}";
        return $"semantic arm capped at {arm.RequestedTopK} examined candidates; "
            + $"{arm.InScopeCandidates} in scope, {arm.Hits.Count} delivered — narrow with {offered}";
    }
}
