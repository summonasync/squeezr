namespace Squeezr.Core.Indexing.Query;

public static class SnippetBudget
{
    // the floor sits above the exact arms' worst ExtractLine output (201 chars estimate to 56
    // tokens), so budget clipping can never reshape an exact-only response — it only fires on
    // hits whose snippet alone would starve the rest of topK
    public const int MinPerHitTokens = 64;

    public const int BreadthDivisor = 8;

    public static int? PerHitTokens(int? maxTokens, int? topK)
    {
        if (maxTokens is not > 0)
        {
            return null;
        }

        var share = topK is > 0 ? maxTokens.Value / topK.Value : maxTokens.Value / BreadthDivisor;
        return Math.Max(MinPerHitTokens, share);
    }

    public static string Clip(string snippet, int perHitTokens)
    {
        var maxChars = Math.Max(1, perHitTokens * 40 / 11);
        return snippet.Length <= maxChars ? snippet : snippet[..(maxChars - 1)] + "…";
    }
}
