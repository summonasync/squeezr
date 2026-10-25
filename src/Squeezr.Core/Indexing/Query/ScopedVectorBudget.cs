namespace Squeezr.Core.Indexing.Query;

public static class ScopedVectorBudget
{
    public const int BaseTopK = 20;

    public const int Ceiling = 160;

    public static int For(int inScopeFiles, int universeFiles)
    {
        if (inScopeFiles >= universeFiles)
        {
            return BaseTopK;
        }

        if (inScopeFiles <= 0)
        {
            return Ceiling;
        }

        var scaled = (int)((BaseTopK * (long)universeFiles + inScopeFiles - 1) / inScopeFiles);
        return Math.Min(Ceiling, Math.Max(BaseTopK, scaled));
    }
}
