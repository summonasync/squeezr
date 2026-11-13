using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class StatusEmbeddingModelTests
{
    private const string ForeignKey = "openai/text-embedding-3-large/1024";

    [Fact]
    public async Task Status_ActiveModelWithForeignVintage_NamesKeyAndCount()
    {
        var root = await NewIndexedWorkspaceAsync("ollama");
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(root)))
        {
            var ids = ChunkIds(store);
            ids.Count.ShouldBeGreaterThan(1);
            store.MarkChunksEmbedded(ids, TestEmbeddingKeys.Calibrated);
            store.MarkChunksEmbedded([ids[0]], ForeignKey);
        }

        var status = await RunCliAsync(root, "status");

        status.ExitCode.ShouldBe(0);
        status.StdOut.ShouldContain("Embedding model: " + TestEmbeddingKeys.Calibrated);
        status.StdOut.ShouldContain("1 chunk(s) carry vectors from another embedding model");
    }

    [Fact]
    public async Task Status_Json_CarriesModelKeyAndForeignCount()
    {
        var root = await NewIndexedWorkspaceAsync("ollama");
        using (var store = new MetaStore(SqueezrPaths.MetaDbPath(root)))
        {
            store.MarkChunksEmbedded(ChunkIds(store), TestEmbeddingKeys.Calibrated);
        }

        var status = await RunCliAsync(root, "status", "--json");

        status.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(status.StdOut.Trim());
        doc.RootElement.GetProperty("embeddingModelKey").GetString().ShouldBe(TestEmbeddingKeys.Calibrated);
        doc.RootElement.GetProperty("otherModelChunks").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Status_UnreachableProvider_SaysNothingAboutTheModel()
    {
        var root = await NewIndexedWorkspaceAsync("auto", deadOllama: true);

        var text = await RunCliAsync(root, "status");
        var json = await RunCliAsync(root, "status", "--json");

        text.ExitCode.ShouldBe(0);
        text.StdOut.ShouldNotContain("Embedding model:");
        text.StdOut.ShouldContain("Embeddings provider: auto");
        using var doc = JsonDocument.Parse(json.StdOut.Trim());
        doc.RootElement.TryGetProperty("embeddingModelKey", out _).ShouldBeFalse();
        doc.RootElement.TryGetProperty("otherModelChunks", out _).ShouldBeFalse();
    }

    private static List<long> ChunkIds(MetaStore store) =>
        [.. store.ListRepos()
            .SelectMany(repo => store.ListFiles(repo.Id))
            .SelectMany(file => store.ListChunksByFile(file.Id))
            .Select(chunk => chunk.Id)];

    private static async Task<string> NewIndexedWorkspaceAsync(string provider, bool deadOllama = false)
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        var config = ConfigFile.Load(root);
        ConfigFile.Save(root, config with
        {
            Embeddings = config.Embeddings with
            {
                Provider = provider,
                Ollama = config.Embeddings.Ollama with
                {
                    Endpoint = deadOllama ? TestWorkspaceConfig.DeadOllamaEndpoint : config.Embeddings.Ollama.Endpoint
                }
            }
        });
        await File.WriteAllTextAsync(
            Path.Combine(root, "Calc.cs"),
            "public class Calc { public int Add(int a, int b) => a + b; public int Sub(int a, int b) => a - b; }");
        (await RunCliAsync(root, "add", ".")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index")).ExitCode.ShouldBe(0);
        return root;
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
