using System.Security.Cryptography;
using System.Text;

namespace Squeezr.Semantic.Embeddings;

public static class ChunkPointId
{
    public static ulong FromHash(string chunkHash)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(chunkHash));
        return BitConverter.ToUInt64(hash, 0);
    }

    public static IReadOnlyList<ulong> Ids(IReadOnlyList<string> hashes) =>
        [.. hashes.Select(FromHash)];

    // A spread over the ids the claim names, one batch, chosen by position rather than by luck so the
    // same store asks the same questions every start.
    public static IReadOnlyList<ulong> Sample(IReadOnlyList<string> hashes, int take)
    {
        var distinct = hashes
            .Distinct(StringComparer.Ordinal)
            .OrderBy(hash => hash, StringComparer.Ordinal)
            .ToList();
        if (distinct.Count <= take)
        {
            return Ids(distinct);
        }

        var ids = new List<ulong>(take);
        var stride = distinct.Count / take;
        for (var seat = 0; seat < take; seat++)
        {
            ids.Add(FromHash(distinct[seat * stride]));
        }

        return ids;
    }
}
