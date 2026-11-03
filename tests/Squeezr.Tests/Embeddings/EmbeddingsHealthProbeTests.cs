using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Squeezr.Cli.Embeddings;
using Squeezr.Core.Configuration;
using Squeezr.Core.Health;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Embeddings;

[Collection("ProcessGlobals")]
public sealed class EmbeddingsHealthProbeTests : IDisposable
{
    private const string HomeEnvironmentVariable = "SQUEEZR_HOME";

    private readonly string _home = TestDirs.NewTempDir();
    private readonly string? _previousEnv;
    private readonly string _root = TestDirs.NewTempDir();

    public EmbeddingsHealthProbeTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _home);
        SqueezrHome.Reset();
        Directory.CreateDirectory(_root);
        new WorkspaceManager(_root).Init("probe-demo");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(HomeEnvironmentVariable, _previousEnv);
        SqueezrHome.Reset();
        try
        {
            Directory.Delete(_root, recursive: true);
            Directory.Delete(_home, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Probe_OllamaOnCpu_ReportsCpuOnly()
    {
        SaveConfig(new EmbeddingsConfig
        {
            Provider = "ollama",
            Ollama = new OllamaOptions { Endpoint = "http://ollama.test" }
        });
        var handler = new JsonStubHandler("""{"models":[{"name":"nomic-embed-text","size":100.0,"size_vram":0.0}]}""");

        var health = await EmbeddingsHealthProbe.ProbeAsync(_root, CancellationToken.None, handler);

        health.Provider.ShouldBe("ollama");
        health.CpuOnly.ShouldNotBeNull().ShouldContain("runs on CPU");
    }

    [Fact]
    public async Task Probe_OllamaOnGpu_StaysQuietOnCpu()
    {
        SaveConfig(new EmbeddingsConfig
        {
            Provider = "ollama",
            Ollama = new OllamaOptions { Endpoint = "http://ollama.test" }
        });
        var handler = new JsonStubHandler("""{"models":[{"name":"nomic-embed-text","size":100.0,"size_vram":100.0}]}""");

        var health = await EmbeddingsHealthProbe.ProbeAsync(_root, CancellationToken.None, handler);

        health.CpuOnly.ShouldBeNull();
    }

    [Fact]
    public async Task Probe_NoSemanticConfig_StaySilent()
    {
        SaveConfig(new EmbeddingsConfig { Provider = "openai" }, noSemantic: true);

        var health = await EmbeddingsHealthProbe.ProbeAsync(_root, CancellationToken.None);

        health.Provider.ShouldBeNull();
        health.ModelProblem.ShouldBeNull();
    }

    [Fact]
    public async Task Probe_UnknownProvider_StaySilent()
    {
        SaveConfig(new EmbeddingsConfig { Provider = "bert" });

        var health = await EmbeddingsHealthProbe.ProbeAsync(_root, CancellationToken.None);

        health.Provider.ShouldBeNull();
        health.ModelProblem.ShouldBeNull();
    }

    private void SaveConfig(EmbeddingsConfig embeddings, bool noSemantic = false)
    {
        var config = ConfigFile.Load(_root);
        ConfigFile.Save(_root, config with
        {
            Embeddings = embeddings,
            Indexing = config.Indexing with { NoSemantic = noSemantic }
        });
    }

    private sealed class JsonStubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
