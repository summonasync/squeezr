using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Squeezr.Core.Storage;

namespace Squeezr.Tests;

public static class TestDirs
{
    private const string TempRootVariable = "SQUEEZR_TEST_TMPROOT";
    private const string BaseFolder = "squeezr-tests";
    private const string LegacyPruneFolder = "squeezr-prune-tests";

    // One store and one client profile per test run, not per invocation: a test that
    // runs `init` and then `status` needs both calls to see the same store. Sharing
    // across the run reproduces what the suite did before, minus the part where "the
    // same store" meant the developer's real one.
    private static readonly Lazy<string> SharedStore = new(NewTempDir);
    private static readonly Lazy<string> SharedClientHome = new(NewTempDir);
    private static readonly ConcurrentDictionary<string, byte> Registered = new(StringComparer.Ordinal);

    // PosixSignalRegistration keeps only a weak reference to itself, so a registration
    // left to the collector silently stops firing — these fields are the keep-alive.
    private static readonly PosixSignalRegistration? Sigint;
    private static readonly PosixSignalRegistration? Sigterm;

    static TestDirs()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveAllRegistered();
        try
        {
            Sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, _ => RemoveAllRegistered());
            Sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => RemoveAllRegistered());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
        }
    }

    public static string SharedStoreDir => SharedStore.Value;

    public static string SharedClientHomeDir => SharedClientHome.Value;

    public static string FixtureBaseRoot() => Path.Combine(ResolvedTempRoot(), BaseFolder);

    public static string LegacyPruneBaseRoot() => Path.Combine(ResolvedTempRoot(), LegacyPruneFolder);

    public static string NewTempDir() => CreateRegisteredLeaf(FixtureBaseRoot());

    // A "normal repo path" fixture root must sit outside BOTH the temp path and the user's home.
    // Discovery walks the ancestors of the start directory, so a home that holds a live .squeezr is a
    // relocation candidate under a pinned SQUEEZR_HOME — and the run then deletes what it moved there
    // (2026-10-19). Only the <guid> leaf is ever removed, never the shared base root.
    public static string NewNonHomeScratchDir()
    {
        var baseDir = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), BaseFolder)
            : "/var/tmp/" + BaseFolder;
        var dir = CreateRegisteredLeaf(baseDir);

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

    internal static bool TryRemoveRegisteredLeaf(string leaf)
    {
        if (!Registered.TryRemove(leaf, out _))
        {
            return false;
        }

        try
        {
            Directory.Delete(leaf, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return true;
    }

    internal static void RemoveAllRegistered()
    {
        foreach (var leaf in Registered.Keys)
        {
            TryRemoveRegisteredLeaf(leaf);
        }
    }

    private static string CreateRegisteredLeaf(string baseDir)
    {
        var dir = Path.Combine(baseDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Registered.TryAdd(dir, 0);
        return dir;
    }

    private static string ResolvedTempRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable(TempRootVariable);
        return string.IsNullOrWhiteSpace(overrideRoot) ? Path.GetTempPath() : overrideRoot;
    }
}
