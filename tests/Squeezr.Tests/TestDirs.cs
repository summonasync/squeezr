using Squeezr.Core.Storage;

namespace Squeezr.Tests;

public static class TestDirs
{
    public static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "squeezr-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // A "normal repo path" fixture root must sit outside BOTH the temp path and the user's home.
    // Discovery walks the ancestors of the start directory, so a home that holds a live .squeezr is a
    // relocation candidate under a pinned SQUEEZR_HOME — and the run then deletes what it moved there
    // (2026-10-19). Only the <guid> leaf is ever removed, never the shared base root.
    public static string NewNonHomeScratchDir()
    {
        var baseDir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "squeezr-tests")
            : "/var/tmp/squeezr-tests";
        var dir = Path.Combine(baseDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var canonical = WorkspaceCanonicalPath.Canonicalize(dir);
        var tempRoot = WorkspaceCanonicalPath.Canonicalize(Path.GetTempPath());
        var homeRoot = WorkspaceCanonicalPath.Canonicalize(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (canonical.StartsWith(tempRoot, StringComparison.Ordinal) ||
            canonical.StartsWith(homeRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"'{dir}' resolves under the temp path '{tempRoot}' or the user home '{homeRoot}'; a fixture " +
                "root has to be outside both to stay a non-temp, non-home path.");
        }

        return dir;
    }
}
