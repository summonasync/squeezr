namespace Squeezr.Core.Indexing.Navigation;

public static class TrigramJaccard
{
    public static double Similarity(string a, string b)
    {
        var setA = Trigrams(a);
        var setB = Trigrams(b);
        if (setA.Count == 0 || setB.Count == 0)
        {
            return 0;
        }

        var intersection = 0;
        foreach (var gram in setA)
        {
            if (setB.Contains(gram))
            {
                intersection++;
            }
        }

        return (double)intersection / (setA.Count + setB.Count - intersection);
    }

    private static HashSet<string> Trigrams(string text)
    {
        var s = text.ToLowerInvariant();
        var set = new HashSet<string>(s.Length);
        for (var i = 0; i + 3 <= s.Length; i++)
        {
            set.Add(s.Substring(i, 3));
        }

        return set;
    }
}
