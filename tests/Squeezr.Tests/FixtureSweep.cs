namespace Squeezr.Tests;

internal static class FixtureSweep
{
    public static void SweepStaleRoots(string baseDir, TimeSpan maxAge, DateTime utcNow)
    {
        if (!Directory.Exists(baseDir))
        {
            return;
        }

        string[] leaves;
        try
        {
            leaves = Directory.GetDirectories(baseDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var leaf in leaves)
        {
            if (!Guid.TryParseExact(Path.GetFileName(leaf), "N", out _) ||
                utcNow - Directory.GetLastWriteTimeUtc(leaf) <= maxAge)
            {
                continue;
            }

            TryDelete(leaf);
        }
    }

    private static void TryDelete(string leaf)
    {
        try
        {
            Directory.Delete(leaf, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
