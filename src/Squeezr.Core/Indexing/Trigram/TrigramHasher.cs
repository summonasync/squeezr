namespace Squeezr.Core.Indexing.Trigram;

public static class TrigramHasher
{
    public static uint ComputeHash(char a, char b, char c) =>
        ((uint)a * 31u + b) * 31u + c;
}
