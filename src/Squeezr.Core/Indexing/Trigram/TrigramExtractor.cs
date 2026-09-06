namespace Squeezr.Core.Indexing.Trigram;

public static class TrigramExtractor
{
    public static Dictionary<uint, List<int>> ExtractPostings(string text)
    {
        var postings = new Dictionary<uint, List<int>>();
        for (var i = 0; i + 3 <= text.Length; i++)
        {
            var hash = TrigramHasher.ComputeHash(text[i], text[i + 1], text[i + 2]);
            if (postings.TryGetValue(hash, out var positions))
            {
                positions.Add(i);
            }
            else
            {
                postings[hash] = [i];
            }
        }

        return postings;
    }
}
