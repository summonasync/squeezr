using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("CliProcessGlobals")]
public sealed class StatusDropQdrantTests
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

    [Fact]
    public async Task Status_QdrantDown_ReportsAvailableFalse_WithExit0()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        PointConfigAtDeadEndpoint(root);

        var status = await RunCliAsync(root, "status", "--json");

        status.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        var qdrant = doc.RootElement.GetProperty("qdrant");
        qdrant.GetProperty("available").GetBoolean().ShouldBeFalse();
        qdrant.GetProperty("pendingChunks").GetInt32().ShouldBe(0);
        qdrant.TryGetProperty("points", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Status_QdrantDown_PrettyPrintsUnavailableSection()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        PointConfigAtDeadEndpoint(root);

        var status = await RunCliAsync(root, "status");

        status.ExitCode.ShouldBe(0);
        status.StdOut.ShouldContain("Qdrant:");
        status.StdOut.ShouldContain("unavailable");
    }

    [Fact]
    public async Task Status_PendingChunks_AreCountedFromMetaStore()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        PointConfigAtDeadEndpoint(root);

        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(root)))
        {
            store.InsertChunks(
            [
                new ChunkDocument { RepoId = 1, FileId = 1, Text = "a", ChunkHash = "h1" },
                new ChunkDocument { RepoId = 1, FileId = 1, Text = "b", ChunkHash = "h2" },
                new ChunkDocument
                {
                    RepoId = 1, FileId = 1, Text = "c", ChunkHash = "h3",
                    EmbeddingStatus = "ok", ModelKey = "m1"
                }
            ]);
        }

        var status = await RunCliAsync(root, "status", "--json");

        status.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        doc.RootElement.GetProperty("qdrant").GetProperty("pendingChunks").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task Status_Json_IncludesConfiguredEmbeddingsProvider()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        PointConfigAtDeadEndpoint(root);

        var status = await RunCliAsync(root, "status", "--json");

        status.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        doc.RootElement.GetProperty("embeddingsProvider").GetString().ShouldBe("auto");
    }

    [Fact]
    public async Task Status_Pretty_ShowsConfiguredEmbeddingsProvider()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        PointConfigAtDeadEndpoint(root);

        var status = await RunCliAsync(root, "status");

        status.ExitCode.ShouldBe(0);
        status.StdOut.ShouldContain("Embeddings provider: auto");
    }

    [Fact]
    public async Task Drop_QdrantDown_DropsLocally_WithWarning_AndExit0()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        PointConfigAtDeadEndpoint(root);

        var drop = await RunCliAsync(root, "drop", "--json");

        drop.ExitCode.ShouldBe(0);
        drop.StdErr.ShouldContain("warning:");
        drop.StdErr.ShouldContain("Qdrant");
        Directory.Exists(SqueezrPaths.StoreDir(root)).ShouldBeFalse();

        using var doc = JsonDocument.Parse(drop.StdOut.Trim());
        doc.RootElement.GetProperty("removed").GetArrayLength().ShouldBe(1);
    }

    private static void PointConfigAtDeadEndpoint(string root) =>
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(root);

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
