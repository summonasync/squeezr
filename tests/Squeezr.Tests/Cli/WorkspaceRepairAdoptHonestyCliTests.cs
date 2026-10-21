using System.Runtime.Versioning;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

// Before the per-orphan verdicts, a repair --yes with an unwritable home and one adoptable orphan died
// in RegistryRepair.AdoptAll with a raw UnauthorizedAccessException: exit 255, no JSON on any channel,
// and no trace of which orphans were adopted and which were not.
[Collection("ProcessGlobals")]
public sealed class WorkspaceRepairAdoptHonestyCliTests
{
    private static readonly UnixFileMode HomeWritable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Yes_ReadOnlyHome_Orphan_Json_ReportsPerOrphanAdoptionFailureInOneDocument()
    {
        var home = TestDirs.NewTempDir();
        var orphanRoot = Canonical(TestDirs.NewTempDir());
        var orphanKey = Key("adopt-blocked", orphanRoot);
        try
        {
            WriteRegistry(home);
            WriteStoreDir(home, orphanKey, "adopt-blocked", orphanRoot);
            File.SetUnixFileMode(home, HomeWritable & ~UnixFileMode.UserWrite);

            var run = await RunAsync(home, "workspace", "repair", "--yes", "--json");

            run.ExitCode.ShouldBe(1);
            using var document = JsonDocument.Parse(run.StdOut.Trim());
            document.RootElement.GetProperty("adopted").EnumerateArray().ShouldBeEmpty();
            var failed = document.RootElement.GetProperty("failedAdoptions").EnumerateArray().Single();
            failed.GetProperty("storeKey").GetString().ShouldBe(orphanKey);
            failed.GetProperty("name").GetString().ShouldBe("adopt-blocked");
            failed.GetProperty("cause").GetString()!.ShouldContain("UnauthorizedAccessException");
            using var error = JsonDocument.Parse(Envelope(run.StdErr));
            var body = error.RootElement.GetProperty("error");
            body.GetProperty("code").GetString().ShouldBe(WorkspaceException.DefaultCode);
            var message = body.GetProperty("message").GetString()!;
            message.ShouldContain("Could not adopt 1 orphaned workspace store(s)");
            message.ShouldContain(orphanKey);
            message.ShouldContain("UnauthorizedAccessException");
            message.ShouldNotContain("registry is busy");
            run.StdErr.ShouldNotContain("at Squeezr");
            Directory.Exists(StoreDir(home, orphanKey)).ShouldBeTrue();
        }
        finally
        {
            Restore(home);
            Clean(home, orphanRoot);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Yes_ReadOnlyHome_Orphan_HumanOutput_NamesEachLostOrphanAndTheVerdict()
    {
        var home = TestDirs.NewTempDir();
        var orphanRoot = Canonical(TestDirs.NewTempDir());
        var orphanKey = Key("adopt-blocked", orphanRoot);
        try
        {
            WriteRegistry(home);
            WriteStoreDir(home, orphanKey, "adopt-blocked", orphanRoot);
            File.SetUnixFileMode(home, HomeWritable & ~UnixFileMode.UserWrite);

            var run = await RunAsync(home, "workspace", "repair", "--yes");

            run.ExitCode.ShouldBe(1);
            run.StdOut.ShouldContain("not adopted: adopt-blocked");
            run.StdOut.ShouldContain(orphanKey);
            run.StdOut.ShouldContain("UnauthorizedAccessException");
            run.StdOut.ShouldNotContain("Nothing to repair");
            run.StdOut.ShouldNotContain("Error:");
            run.StdErr.ShouldContain("error: Could not adopt 1 orphaned workspace store(s)");
            run.StdErr.ShouldNotContain("registry is busy");
            Directory.Exists(StoreDir(home, orphanKey)).ShouldBeTrue();
        }
        finally
        {
            Restore(home);
            Clean(home, orphanRoot);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task AdoptWithRoot_ReadOnlyHome_Json_ReportsEnvelopeInsteadOfARawExit()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        var storeKey = Key("legacy-adopt", Canonical(root));
        try
        {
            WriteRegistry(home);
            WriteStoreDir(home, storeKey, "legacy-adopt", null);
            File.SetUnixFileMode(home, HomeWritable & ~UnixFileMode.UserWrite);

            var run = await RunAsync(
                home, "workspace", "repair", "--store-key", storeKey, "--root", root, "--json");

            run.ExitCode.ShouldBe(1);
            run.StdOut.ShouldBeEmpty();
            using var error = JsonDocument.Parse(Envelope(run.StdErr));
            var body = error.RootElement.GetProperty("error");
            body.GetProperty("code").GetString().ShouldBe(WorkspaceException.DefaultCode);
            body.GetProperty("message").GetString()!.ShouldContain("Access to the path");
            run.StdErr.ShouldNotContain("registry is busy");
        }
        finally
        {
            Restore(home);
            Clean(home, root);
        }
    }

    [LinuxFact]
    [SupportedOSPlatform("linux")]
    public async Task Yes_ReadOnlyHome_TwoOrphans_LoopContinues_EnvelopeCountsBoth()
    {
        var home = TestDirs.NewTempDir();
        var orphanRootA = Canonical(TestDirs.NewTempDir());
        var orphanRootB = Canonical(TestDirs.NewTempDir());
        var keyA = Key("adopt-blocked-a", orphanRootA);
        var keyB = Key("adopt-blocked-b", orphanRootB);
        try
        {
            WriteRegistry(home);
            WriteStoreDir(home, keyA, "adopt-blocked-a", orphanRootA);
            WriteStoreDir(home, keyB, "adopt-blocked-b", orphanRootB);
            File.SetUnixFileMode(home, HomeWritable & ~UnixFileMode.UserWrite);

            var run = await RunAsync(home, "workspace", "repair", "--yes", "--json");

            run.ExitCode.ShouldBe(1);
            using var document = JsonDocument.Parse(run.StdOut.Trim());
            var failed = document.RootElement.GetProperty("failedAdoptions");
            failed.GetArrayLength().ShouldBe(2);
            failed.EnumerateArray()
                .Select(row => row.GetProperty("storeKey").GetString())
                .OrderBy(key => key, StringComparer.Ordinal)
                .ShouldBe(new[] { keyA, keyB }.OrderBy(key => key, StringComparer.Ordinal).ToList());
            using var error = JsonDocument.Parse(Envelope(run.StdErr));
            var message = error.RootElement.GetProperty("error").GetProperty("message").GetString()!;
            message.ShouldContain("Could not adopt 2 orphaned workspace store(s)");
            message.ShouldContain(keyA);
            message.ShouldContain(keyB);
            Directory.Exists(StoreDir(home, keyA)).ShouldBeTrue();
            Directory.Exists(StoreDir(home, keyB)).ShouldBeTrue();
        }
        finally
        {
            Restore(home);
            Clean(home, orphanRootA, orphanRootB);
        }
    }

    private static string Envelope(string stdErr) =>
        stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Last(line => line.TrimStart().StartsWith('{'));

    private static void WriteRegistry(string home, params WorkspaceRegistryEntry[] entries) =>
        File.WriteAllText(
            Path.Combine(home, SqueezrPaths.RegistryFileName),
            JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));

    private static void WriteStoreDir(string home, string storeKey, string name, string? rootPath)
    {
        var storeDir = StoreDir(home, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(
                new WorkspaceManifest { Name = name, RootPath = rootPath },
                SqueezrJson.FileOptions));
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName), new string('x', 256));
    }

    [SupportedOSPlatform("linux")]
    private static void Restore(string home)
    {
        if (Directory.Exists(home))
        {
            File.SetUnixFileMode(home, HomeWritable);
        }
    }

    private static string Key(string name, string canonicalRoot) => WorkspaceStoreKeys.Create(name, canonicalRoot);

    private static string StoreDir(string home, string storeKey) =>
        Path.Combine(home, SqueezrPaths.WorkspacesDirName, storeKey);

    private static string Canonical(string path) => WorkspaceCanonicalPath.Canonicalize(path);

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string home, params string[] args) =>
        CliTestProcess.RunWithHomeAsync(home, TestDirs.NewTempDir(), args);

    private static void Clean(string home, params string[] extraDirs)
    {
        foreach (var dir in new[] { home }.Concat(extraDirs))
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
