namespace Squeezr.Core.Indexing.Trigram;

public static class PostingListIntersect
{
    public static uint[] Intersect(IReadOnlyList<uint[]> sortedLists)
    {
        if (sortedLists.Count == 0)
        {
            return [];
        }

        var order = sortedLists
            .Select((list, index) => (Length: list.Length, Index: index))
            .OrderBy(x => x.Length)
            .ToArray();

        var current = sortedLists[order[0].Index];
        for (var i = 1; i < order.Length && current.Length > 0; i++)
        {
            current = IntersectPair(current, sortedLists[order[i].Index]);
        }

        return current;
    }

    public static uint[] IntersectPair(uint[] a, uint[] b)
    {
        var result = new List<uint>(Math.Min(a.Length, b.Length));
        var i = 0;
        var j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] < b[j])
            {
                i++;
            }
            else if (a[i] > b[j])
            {
                j++;
            }
            else
            {
                result.Add(a[i]);
                i++;
                j++;
            }
        }

        return result.ToArray();
    }
}
