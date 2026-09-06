namespace Squeezr.Core.Indexing.Query;

public static class TokenCounter
{
    public static int Estimate(string text) =>
        Math.Max(1, (int)Math.Ceiling(text.Length / 4.0 * 1.1));
}
