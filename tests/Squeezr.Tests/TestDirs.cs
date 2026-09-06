namespace Squeezr.Tests;

public static class TestDirs
{
    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "squeezr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
