using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Indexing;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Mcp;

[Collection("ProcessGlobals")]
public sealed class IndexStatusModelKeyTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";
    private const string ForeignKey = "openai/text-embedding-3-large/1024";

    private readonly string? _previousHome;
    private readonly string _home;
    private readonly List<string> _created = [];

    public IndexStatusModelKeyTests()
    {
        _previousHome = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        _home = TestDirs.NewTempDir();
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousHome);
        SqueezrHome.Reset();
        foreach (var dir in _created)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task IndexStatus_TwoVintagesInStore_ReportsKeyAndForeignCount()
    {
        var workspace = NewIndexedWorkspace(provider: "ollama");
        var chunks = ChunkIds(workspace);
        using (var store = OpenStore(workspace))
        {
            store.MarkChunksEmbedded(chunks[..2], ForeignKey);
        }

        var embeddings = (await CallIndexStatusAsync(workspace)).GetProperty("embeddings");

        embeddings.GetProperty("modelKey").GetString().ShouldBe(TestEmbeddingKeys.Calibrated);
        embeddings.GetProperty("otherModelChunks").GetInt32().ShouldBe(2);
        chunks.Count.ShouldBeGreaterThan(2);
    }

    [Fact]
    public async Task IndexStatus_SingleVintage_ReportsZeroForeignChunks()
    {
        var workspace = NewIndexedWorkspace(provider: "ollama");
        using (var store = OpenStore(workspace))
        {
            store.MarkChunksEmbedded(ChunkIds(workspace), TestEmbeddingKeys.Calibrated);
        }

        var embeddings = (await CallIndexStatusAsync(workspace)).GetProperty("embeddings");

        embeddings.GetProperty("modelKey").GetString().ShouldBe(TestEmbeddingKeys.Calibrated);
        embeddings.GetProperty("otherModelChunks").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task IndexStatus_UnreachableProvider_OmitsBothModelFields()
    {
        var workspace = NewIndexedWorkspace(provider: "auto", deadOllama: true);

        var embeddings = (await CallIndexStatusAsync(workspace)).GetProperty("embeddings");

        embeddings.TryGetProperty("modelKey", out _).ShouldBeFalse();
        embeddings.TryGetProperty("otherModelChunks", out _).ShouldBeFalse();
        embeddings.GetProperty("pending").GetInt32().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task IndexStatus_UnsupportedProvider_OmitsBothModelFields()
    {
        var workspace = NewIndexedWorkspace(provider: "ollama");
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with
        {
            Embeddings = config.Embeddings with { Provider = "onnx" }
        });

        var embeddings = (await CallIndexStatusAsync(workspace)).GetProperty("embeddings");

        embeddings.TryGetProperty("modelKey", out _).ShouldBeFalse();
        embeddings.TryGetProperty("otherModelChunks", out _).ShouldBeFalse();
    }

    private async Task<JsonElement> CallIndexStatusAsync(string workspace)
    {
        await using var harness = await McpTestHarness.StartAsync(workspace);
        var result = await harness.Client.CallToolAsync(
            "index_status", new Dictionary<string, object?>(), cancellationToken: CancellationToken.None);

        result.IsError.ShouldNotBe(true);
        var json = result.Content.ShouldHaveSingleItem().ToString()!;
        return JsonDocument.Parse(json).RootElement.GetProperty("data").Clone();
    }

    private string NewIndexedWorkspace(string provider, bool deadOllama = false)
    {
        var workspace = Track(TestDirs.NewTempDir());
        Directory.CreateDirectory(Path.Combine(workspace, "repo"));
        var manager = new WorkspaceManager(workspace);
        manager.Init("model-key-demo");
        TestWorkspaceConfig.PointQdrantAtDeadEndpoint(workspace);
        var config = ConfigFile.Load(workspace);
        ConfigFile.Save(workspace, config with
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
        manager.AddRepo("repo", "demo");
        File.WriteAllText(Path.Combine(workspace, "repo", "Alpha.cs"), """
            namespace Demo;

            public class Alpha
            {
                public string Term = "visneedle";

                public int Add(int a, int b)
                {
                    return a + b;
                }
            }

            public class Beta
            {
                public int Sub(int a, int b)
                {
                    return a - b;
                }
            }
            """);
        new WorkspaceIndexer().Index(workspace);
        return workspace;
    }

    private static List<long> ChunkIds(string workspace)
    {
        using var store = OpenStore(workspace);
        return [.. store.ListRepos()
            .SelectMany(repo => store.ListFiles(repo.Id))
            .SelectMany(file => store.ListChunksByFile(file.Id))
            .Select(chunk => chunk.Id)];
    }

    private static MetaStore OpenStore(string workspace) => new(SqueezrPaths.MetaDbPath(workspace));

    private string Track(string dir)
    {
        _created.Add(dir);
        return dir;
    }
}
