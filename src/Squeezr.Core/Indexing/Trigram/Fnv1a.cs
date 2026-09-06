namespace Squeezr.Core.Indexing.Trigram;

public static class Fnv1a
{
    public const uint Seed = 2166136261u;

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var hash = Seed;
        foreach (var b in data)
        {
            hash ^= b;
            hash *= 16777619;
        }

        return hash;
    }
}
