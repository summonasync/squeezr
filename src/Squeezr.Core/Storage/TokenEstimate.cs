namespace Squeezr.Core.Storage;

public static class TokenEstimate
{
    public static long WholeFile(long sizeBytes) =>
        Math.Max(1, (long)Math.Ceiling(sizeBytes / 4.0 * 1.1));
}
