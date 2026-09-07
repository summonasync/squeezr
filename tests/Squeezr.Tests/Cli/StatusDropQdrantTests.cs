using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

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
        doc.RootElement.GetProperty("removed").GetArrayLength().ShouldBe(3);
    }

    private static void PointConfigAtDeadEndpoint(string root)
    {
        var config = ConfigFile.Load(root) with
        {
            Qdrant = new QdrantConfig { Endpoint = "http://localhost:1" }
        };
        ConfigFile.Save(root, config);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(string workingDir, params string[] args)
    {
        var startInfo = new ProcessStartInfo("dotnet", $"\"{CliDll}\" {string.Join(" ", args)}")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
