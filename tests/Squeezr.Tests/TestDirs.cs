namespace Squeezr.Tests;

public static class TestDirs
{
    // One store and one client profile per test run, not per invocation: a test that
    // runs `init` and then `status` needs both calls to see the same store. Sharing
    // across the run reproduces what the suite did before, minus the part where "the
    // same store" meant the developer's real one.
    private static readonly Lazy<string> SharedStore = new(NewTempDir);
    private static readonly Lazy<string> SharedClientHome = new(NewTempDir);

    public static string SharedStoreDir => SharedStore.Value;

    public static string SharedClientHomeDir => SharedClientHome.Value;

    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "squeezr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
