using System.Text.Json;
using Shouldly;
using Squeezr.Core.Serialization;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Infra;

// Proof-by-example for the documented pinned pattern: a scratch init under a pinned home must leave
// every other home's registry byte-identical and land exactly one row in the pinned one.
[Collection("CliProcessGlobals")]
public sealed class PinnedScratchInitProofTests
{
    [Fact]
    public async Task Init_UnderPinnedHome_LeavesStandInLiveRegistryUntouched()
    {
        var liveHome = TestDirs.NewTempDir();
        var pinnedHome = TestDirs.NewTempDir();
        var scratchRoot = TestDirs.NewTempDir();
        try
        {
            var before = RegistryBytes(liveHome, Row("kept-a"), Row("kept-b"));

            var run = await CliTestProcess.RunWithHomeAsync(pinnedHome, scratchRoot, "init", "--name", "scratch-proof");

            run.ExitCode.ShouldBe(0);
            RegistryBytes(liveHome).ShouldBe(before);
            ReadRegistry(pinnedHome).ShouldHaveSingleItem().Name.ShouldBe("scratch-proof");
        }
        finally
        {
            foreach (var dir in new[] { liveHome, pinnedHome, scratchRoot })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        }
    }

    private static byte[] RegistryBytes(string home, params WorkspaceRegistryEntry[] entries)
    {
        var path = Path.Combine(home, SqueezrPaths.RegistryFileName);
        if (entries.Length > 0)
        {
            File.WriteAllText(path, JsonSerializer.Serialize(entries, SqueezrJson.FileOptions));
        }

        return File.Exists(path) ? File.ReadAllBytes(path) : [];
    }

    private static WorkspaceRegistryEntry Row(string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRegistryEntry(
            name,
            $"/nonexistent/squeezr-proof/{name}",
            WorkspaceStoreKeys.Create(name, $"/nonexistent/squeezr-proof/{name}"),
            now,
            now);
    }

    private static List<WorkspaceRegistryEntry> ReadRegistry(string home) =>
        JsonSerializer.Deserialize<List<WorkspaceRegistryEntry>>(
            File.ReadAllText(Path.Combine(home, SqueezrPaths.RegistryFileName)),
            SqueezrJson.FileOptions) ?? [];
}
