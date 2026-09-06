namespace Squeezr.Core.Indexing.Query;

public static class CaseVariants
{
    public static string[] Expand(string trigram)
    {
        var chars = trigram.ToLowerInvariant().ToCharArray();
        var count = chars.Length;
        var total = 1 << count;
        var popcounts = new int[total];
        for (var mask = 1; mask < total; mask++)
            popcounts[mask] = popcounts[mask >> 1] + (mask & 1);

        var variants = new List<string>(total);
        var ordered = Enumerable.Range(0, total).OrderByDescending(m => popcounts[m]).ThenBy(m => m);
        foreach (var mask in ordered)
        {
            var variant = (char[])chars.Clone();
            for (var bit = 0; bit < count; bit++)
            {
                if ((mask & (1 << bit)) == 0)
                    variant[bit] = char.ToUpperInvariant(variant[bit]);
            }

            variants.Add(new string(variant));
        }

        return variants.Distinct(StringComparer.Ordinal).ToArray();
    }
}
