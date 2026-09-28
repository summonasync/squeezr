using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class InitAdoptionHintTests
{
    [Fact]
    public async Task Init_OverAdoptedOrphan_WithNameMismatchNeighbor_PrintsRepairHintToStderrAndExitsZero()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(home, SqueezrPaths.RegistryFileName), "[]");
            var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(root);
            var adoptedKey = WorkspaceStoreKeys.Create("alpha", canonicalRoot);
            var neighborKey = WorkspaceStoreKeys.Create("beta", canonicalRoot);
            WriteStoreManifest(home, adoptedKey, "alpha", canonicalRoot);
            WriteStoreManifest(home, neighborKey, "beta", canonicalRoot);

            var result = await CliTestProcess.RunWithHomeAsync(home, root, "init", "--name", "alpha");

            result.ExitCode.ShouldBe(0);
            result.StdErr.ShouldContain(neighborKey);
            result.StdErr.ShouldContain("squeezr workspace repair");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Init_OverAdoptedOrphan_WithoutNeighbor_ExitsZeroWithoutHint()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(home, SqueezrPaths.RegistryFileName), "[]");
            var canonicalRoot = WorkspaceCanonicalPath.Canonicalize(root);
            var adoptedKey = WorkspaceStoreKeys.Create("solo", canonicalRoot);
            WriteStoreManifest(home, adoptedKey, "solo", canonicalRoot);

            var result = await CliTestProcess.RunWithHomeAsync(home, root, "init", "--name", "solo");

            result.ExitCode.ShouldBe(0);
            result.StdErr.ShouldNotContain("workspace repair");
        }
        finally
        {
            Directory.Delete(home, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteStoreManifest(string home, string storeKey, string name, string rootPath)
    {
        var storeDir = Path.Combine(home, SqueezrPaths.WorkspacesDirName, storeKey);
        Directory.CreateDirectory(storeDir);
        File.WriteAllText(
            Path.Combine(storeDir, SqueezrPaths.StoreManifestFileName),
            JsonSerializer.Serialize(
                new WorkspaceManifest { Name = name, RootPath = rootPath },
                SqueezrJson.FileOptions));
    }
}
