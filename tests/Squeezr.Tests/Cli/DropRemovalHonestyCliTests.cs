using System.Runtime.Versioning;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

// review-067-08 W-1/W-2: the store dir can disappear while its registry row stays. Before the fix that
// half-applied removal printed "Error: Access to the path '…/workspaces.lock' is denied." on stdout with
// exit 255 — no JSON, and no trace of the path that was already deleted.
[Collection("ProcessGlobals")]
public sealed class DropRemovalHonestyCliTests
{
    private static readonly UnixFileMode HomeWritable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Drop_ReadOnlyHome_Json_ReportsUnverifiedEnvelopeWithTheGonePath()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        const string StoreKey = "gone-1a2b3c4d";
        var storeDir = StoreDir(home, StoreKey);
        try
        {
            WriteRegistry(home, Row("gone", Canonical(root), StoreKey));
            WriteStoreDir(home, StoreKey);
            File.SetUnixFileMode(home, HomeWritable & ~UnixFileMode.UserWrite);

            var drop = await RunAsync(home, root, "drop", "--store-key", StoreKey, "--json");

            drop.ExitCode.ShouldBe(1);
            drop.StdOut.ShouldBeEmpty();
            using var error = JsonDocument.Parse(Envelope(drop.StdErr));
            var body = error.RootElement.GetProperty("error");
            body.GetProperty("code").GetString().ShouldBe(WorkspaceException.DefaultCode);
            body.GetProperty("message").GetString()!.ShouldContain("removed: unverified");
            body.GetProperty("removedPaths").EnumerateArray().Select(path => path.GetString())
                .ShouldBe([storeDir]);
            body.GetProperty("message").GetString()!.ShouldNotContain("Could not remove the store dir");
            drop.StdErr.ShouldNotContain("registry is busy");
            Directory.Exists(storeDir).ShouldBeFalse();
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(StoreKey);
        }
        finally
        {
            Clean(home, root);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Drop_ReadOnlyHome_HumanOutput_ReportsUnverifiedOnStdErrOnly()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        const string StoreKey = "gone-1a2b3c4d";
        var storeDir = StoreDir(home, StoreKey);
        try
        {
            WriteRegistry(home, Row("gone", Canonical(root), StoreKey));
            WriteStoreDir(home, StoreKey);
            File.SetUnixFileMode(home, HomeWritable & ~UnixFileMode.UserWrite);

            var drop = await RunAsync(home, root, "drop", "--store-key", StoreKey);

            drop.ExitCode.ShouldBe(1);
            drop.StdOut.ShouldNotContain("Error:");
            drop.StdErr.ShouldContain("error: ");
            drop.StdErr.ShouldContain("removed: unverified");
            drop.StdErr.ShouldContain(storeDir);
            drop.StdErr.ShouldNotContain("at Squeezr");
            Directory.Exists(storeDir).ShouldBeFalse();
        }
        finally
        {
            Clean(home, root);
        }
    }

    [Fact]
    public async Task Drop_RegistryLockHeldAfterStoreRemoval_Json_ReportsUnverifiedNotBusy()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        const string StoreKey = "busy-1a2b3c4d";
        var storeDir = StoreDir(home, StoreKey);
        using var registryLock = new FileStream(
            Path.Combine(home, SqueezrPaths.RegistryLockFileName),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        try
        {
            WriteRegistry(home, Row("busy", Canonical(root), StoreKey));
            WriteStoreDir(home, StoreKey);

            var drop = await RunAsync(home, root, "drop", "--store-key", StoreKey, "--json");

            drop.ExitCode.ShouldBe(1);
            drop.StdOut.ShouldBeEmpty();
            using var error = JsonDocument.Parse(Envelope(drop.StdErr));
            var body = error.RootElement.GetProperty("error");
            body.GetProperty("message").GetString()!.ShouldContain("removed: unverified");
            body.GetProperty("removedPaths").EnumerateArray().Select(path => path.GetString())
                .ShouldBe([storeDir]);
            drop.StdErr.ShouldNotContain("registry is busy");
            Directory.Exists(storeDir).ShouldBeFalse();
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(StoreKey);
        }
        finally
        {
            Clean(home, root);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Drop_CurrentWorkspace_ReadOnlyHome_Json_ReportsUnverifiedForTheStoreItDeleted()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        try
        {
            (await RunAsync(home, root, "init", "--name", "cwd-drop", "--json")).ExitCode.ShouldBe(0);
            var storeKey = ReadRegistry(home).ShouldHaveSingleItem().StoreKey;
            var storeDir = StoreDir(home, storeKey);
            Directory.Exists(storeDir).ShouldBeTrue();
            File.SetUnixFileMode(home, HomeWritable & ~UnixFileMode.UserWrite);

            var drop = await RunAsync(home, root, "drop", "--json");

            drop.ExitCode.ShouldBe(1);
            drop.StdOut.ShouldBeEmpty();
            using var error = JsonDocument.Parse(Envelope(drop.StdErr));
            var body = error.RootElement.GetProperty("error");
            body.GetProperty("message").GetString()!.ShouldContain("removed: unverified");
            body.GetProperty("removedPaths").EnumerateArray().Select(path => path.GetString())
                .ShouldBe([storeDir]);
            Directory.Exists(storeDir).ShouldBeFalse();
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(storeKey);
        }
        finally
        {
            Clean(home, root);
        }
    }

    // The cwd route never derives a store key, so its store-phase verdict names the dir it could not
    // remove instead of a key — and the guard upstream must not repaint it as a busy registry.
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Drop_CurrentWorkspace_UndeletableStoreDir_Json_NamesTheStorePhaseNotAKey()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        try
        {
            (await RunAsync(home, root, "init", "--name", "cwd-store-phase", "--json")).ExitCode.ShouldBe(0);
            var storeKey = ReadRegistry(home).ShouldHaveSingleItem().StoreKey;
            var storeDir = StoreDir(home, storeKey);
            File.SetUnixFileMode(storeDir, UnixFileMode.None);

            var drop = await RunAsync(home, root, "drop", "--json");

            drop.ExitCode.ShouldBe(1);
            drop.StdOut.ShouldBeEmpty();
            using var error = JsonDocument.Parse(Envelope(drop.StdErr));
            var body = error.RootElement.GetProperty("error");
            body.GetProperty("code").GetString().ShouldBe(WorkspaceException.DefaultCode);
            var message = body.GetProperty("message").GetString()!;
            message.ShouldContain("Could not remove the workspace store dir");
            message.ShouldContain(storeDir);
            message.ShouldNotContain("store key");
            body.TryGetProperty("removedPaths", out _).ShouldBeFalse();
            drop.StdErr.ShouldNotContain("registry is busy");
            Directory.Exists(storeDir).ShouldBeTrue();
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(storeKey);
        }
        finally
        {
            UnlockStoreDirs(home);
            Clean(home, root);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Drop_CurrentWorkspace_UndeletableStoreDir_HumanOutput_NamesTheStoreDirOnStdErr()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        try
        {
            (await RunAsync(home, root, "init", "--name", "cwd-store-phase", "--json")).ExitCode.ShouldBe(0);
            var storeDir = StoreDir(home, ReadRegistry(home).ShouldHaveSingleItem().StoreKey);
            File.SetUnixFileMode(storeDir, UnixFileMode.None);

            var drop = await RunAsync(home, root, "drop");

            drop.ExitCode.ShouldBe(1);
            drop.StdOut.ShouldBeEmpty();
            drop.StdErr.ShouldContain("error: Could not remove the workspace store dir");
            drop.StdErr.ShouldContain(storeDir);
            drop.StdErr.ShouldNotContain("registry is busy");
            Directory.Exists(storeDir).ShouldBeTrue();
        }
        finally
        {
            UnlockStoreDirs(home);
            Clean(home, root);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Drop_StoreDirUndeletable_Json_AddsNoRemovedPathsToTheEnvelope()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        const string StoreKey = "blocked-1a2b3c4d";
        var storeDir = StoreDir(home, StoreKey);
        try
        {
            WriteRegistry(home, Row("blocked", Canonical(root), StoreKey));
            WriteStoreDir(home, StoreKey);
            File.SetUnixFileMode(storeDir, UnixFileMode.None);

            var drop = await RunAsync(home, root, "drop", "--store-key", StoreKey, "--json");

            drop.ExitCode.ShouldBe(1);
            using var error = JsonDocument.Parse(Envelope(drop.StdErr));
            var body = error.RootElement.GetProperty("error");
            body.TryGetProperty("removedPaths", out _).ShouldBeFalse();
            body.GetProperty("message").GetString()!.ShouldNotContain("removed: unverified");
            body.GetProperty("message").GetString()!.ShouldContain("Could not remove the store dir");
            Directory.Exists(storeDir).ShouldBeTrue();
            ReadRegistry(home).ShouldHaveSingleItem().StoreKey.ShouldBe(StoreKey);
        }
        finally
        {
            UnlockStoreDirs(home);
            Clean(home, root);
        }
    }

    // The permission net in Execute: a registry file the process cannot read throws an
    // UnauthorizedAccessException that no guard repaints. Without the net the CLI dies with exit 255 and
    // puts "Error: …" on stdout, which breaks the --json contract. Nothing was removed here, so no path
    // may be claimed either.
    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Drop_UnreadableRegistryFile_Json_ReportsEnvelopeInsteadOfTwoFifty()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        try
        {
            WriteRegistry(home, Row("unreadable", Canonical(root), "unreadable-1a2b3c4d"));
            File.SetUnixFileMode(Path.Combine(home, SqueezrPaths.RegistryFileName), UnixFileMode.None);

            var drop = await RunAsync(home, root, "drop", "--store-key", "unreadable-1a2b3c4d", "--json");

            drop.ExitCode.ShouldBe(1);
            drop.StdOut.ShouldBeEmpty();
            using var error = JsonDocument.Parse(Envelope(drop.StdErr));
            var body = error.RootElement.GetProperty("error");
            body.GetProperty("code").GetString().ShouldBe(WorkspaceException.DefaultCode);
            body.TryGetProperty("removedPaths", out _).ShouldBeFalse();
            body.GetProperty("message").GetString()!.ShouldNotContain("removed: unverified");
            drop.StdErr.ShouldNotContain("at Squeezr");
        }
        finally
        {
            Clean(home, root);
        }
    }

    [SupportedOSPlatform("linux")]
    private static void UnlockStoreDirs(string home)
    {
        var workspaces = Path.Combine(home, SqueezrPaths.WorkspacesDirName);
        foreach (var dir in Directory.EnumerateDirectories(workspaces))
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string Envelope(string stdErr) =>
        stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Last(line => line.TrimStart().StartsWith('{'));

    private static WorkspaceRegistryEntry Row(string name, string canonicalRoot, string storeKey)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRegistryEntry(name, canonicalRoot, storeKey, now, now);
    }

    private static string StoreDir(string home, string storeKey) =>
        Path.Combine(home, SqueezrPaths.WorkspacesDirName, storeKey);

    private static void WriteStoreDir(string home, string storeKey)
    {
        var storeDir = StoreDir(home, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName), new string('x', 64));
    }

    private static void WriteRegistry(string home, params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(
            Path.Combine(home, SqueezrPaths.RegistryFileName),
            JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private static List<WorkspaceRegistryEntry> ReadRegistry(string home) =>
        JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(
            File.ReadAllText(Path.Combine(home, SqueezrPaths.RegistryFileName)), SqueezrJson.FileOptions) ?? [];

    private static string Canonical(string path) => WorkspaceCanonicalPath.Canonicalize(path);

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string home, string workingDir, params string[] args) =>
        CliTestProcess.RunWithHomeAsync(home, workingDir, args);

    private static void Clean(string home, params string[] extraDirs)
    {
        foreach (var dir in new[] { home }.Concat(extraDirs))
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(dir, HomeWritable);
            }

            Directory.Delete(dir, recursive: true);
        }
    }
}
