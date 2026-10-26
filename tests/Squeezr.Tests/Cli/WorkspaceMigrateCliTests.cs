using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class WorkspaceMigrateCliTests
{
    [Fact]
    public async Task Migrate_StructuralStore_WithoutYes_RefusesAndTouchesNothing()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        WriteStructuralStore(root, withMetaDb: true);
        try
        {
            var run = await CliTestProcess.RunWithHomeAsync(home, home, "workspace", "migrate", root, "--json");

            run.ExitCode.ShouldBe(1);
            run.StdOut.ShouldBeEmpty();
            var body = ErrorBody(run.StdErr);
            body.GetProperty("code").GetString().ShouldBe("invalid_usage");
            body.GetProperty("message").GetString()!.ShouldContain("Refusing to relocate");
            body.GetProperty("message").GetString()!.ShouldContain("--yes");
            File.Exists(Path.Combine(root, SqueezrPaths.StoreDirName, SqueezrPaths.RegistryFileName)).ShouldBeTrue();
            File.Exists(Path.Combine(home, SqueezrPaths.RegistryFileName)).ShouldBeFalse();
        }
        finally
        {
            Cleanup(home, root);
        }
    }

    [Fact]
    public async Task Migrate_StructuralStore_WithYes_Json_MovesAndCounts()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        WriteStructuralStore(root, withMetaDb: true);
        try
        {
            var run = await CliTestProcess.RunWithHomeAsync(
                home, home, "workspace", "migrate", root, "--yes", "--json");

            run.ExitCode.ShouldBe(0);
            run.StdErr.ShouldBeEmpty();
            using var document = JsonDocument.Parse(run.StdOut.Trim());
            var payload = document.RootElement;
            payload.GetProperty("sourceDir").GetString()!.ShouldBe(Path.Combine(root, SqueezrPaths.StoreDirName));
            payload.GetProperty("movedFileCount").GetInt32().ShouldBe(5);
            var destination = payload.GetProperty("destinationDir").GetString();
            destination.ShouldNotBeNull();
            File.Exists(Path.Combine(destination!, SqueezrPaths.MetaDbFileName)).ShouldBeTrue();
            Directory.Exists(Path.Combine(root, SqueezrPaths.StoreDirName)).ShouldBeFalse();
        }
        finally
        {
            Cleanup(home, root);
        }
    }

    [Fact]
    public async Task Migrate_StructuralStore_WithYes_HumanOutput_PrintsSourceDestinationAndCount()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        WriteStructuralStore(root, withMetaDb: false);
        try
        {
            var run = await CliTestProcess.RunWithHomeAsync(home, home, "workspace", "migrate", root, "--yes");

            run.ExitCode.ShouldBe(0);
            run.StdOut.ShouldContain(Path.Combine(root, SqueezrPaths.StoreDirName));
            run.StdOut.ShouldContain("4 file(s)");
        }
        finally
        {
            Cleanup(home, root);
        }
    }

    private static void WriteStructuralStore(string root, bool withMetaDb)
    {
        File.WriteAllText(Path.Combine(root, SqueezrPaths.ManifestFileName), """{"name":"legacy","repos":[]}""");
        var storeDir = Path.Combine(root, SqueezrPaths.StoreDirName);
        Directory.CreateDirectory(Path.Combine(storeDir, SqueezrPaths.WorkspacesDirName, "w1"));
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.WorkspacesDirName, "w1", "x.bin"), "chunk");
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.RegistryFileName), "[]");
        File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.ConfigFileName), "{}");
        if (withMetaDb)
        {
            File.WriteAllText(Path.Combine(storeDir, SqueezrPaths.MetaDbFileName), "index payload");
        }
    }

    private static JsonElement ErrorBody(string stdErr)
    {
        var jsonLine = stdErr
            .Split('\n')
            .Last(line => line.TrimStart().StartsWith('{'));
        using var document = JsonDocument.Parse(jsonLine);
        return document.RootElement.GetProperty("error").Clone();
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (var dir in dirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
