namespace Squeezr.Core.Indexing.Query;

public static class RankingWeights
{
    public const double FilenameMatchBonus = 1000.0;

    public const double BodyMatchBase = 100.0;

    public const double TestFileMultiplier = 0.3;

    public const double SizeBonusCap = 10.0;

    public const long SizeBonusReferenceBytes = 50 * 1024;

    public const int CaseVariantCap = 64;
}
