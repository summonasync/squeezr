using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;
using Squeezr.Semantic.Configuration;

namespace Squeezr.Tests.Cli;

// SQUEEZR_HOME stays untouched: the ambient env at CliTestProcess first-touch time becomes the
// run-wide central store for every spawned child, so a ctor-scoped temp home here (deleted on
// dispose) would poison that snapshot for every test class running later in the run.
[Collection("ProcessGlobals")]
public sealed class DoctorCommandTests
{
    private readonly string _home = TestDirs.NewTempDir();

    public DoctorCommandTests()
    {
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, SqueezrPaths.RegistryFileName), "[]");
    }

    [Fact]
    public async Task Doctor_OnEmptyMachine_ReportsServicesFailureAndExitsOne()
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunPipedAsync(_home, "", root, "init", "--json")).ExitCode.ShouldBe(0);

        var run = await CliTestProcess.RunPipedAsync(_home, "", root, "doctor", "--json");

        run.ExitCode.ShouldBe(1);
        var doc = JsonDocument.Parse(run.StdOut);
        doc.RootElement.GetProperty("kind").GetString().ShouldBe("doctor");
        doc.RootElement.GetProperty("healthy").GetBoolean().ShouldBeFalse();
        doc.RootElement.GetProperty("checks").EnumerateArray()
            .ShouldContain(c => c.GetProperty("name").GetString() == "services");
    }

    [Fact]
    public async Task Doctor_TextRender_NamesRemedyAndChangesNothing()
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunPipedAsync(_home, "", root, "init", "--json")).ExitCode.ShouldBe(0);

        var run = await CliTestProcess.RunPipedAsync(_home, "", root, "doctor");

        run.ExitCode.ShouldBe(1);
        run.StdOut.ShouldContain("fail");
        run.StdOut.ShouldContain(DoctorRemedyText);
        run.StdOut.ShouldContain("nothing was changed");
    }

    // the model key this fixture invents: no other workspace in the run-wide registry can name it,
    // so the finding is pinned to this stand even though doctor walks every registered workspace.
    private const string UniqueKey = "openai/vis-112t4/42";

    [Fact]
    public async Task Doctor_ActiveEmbeddingModel_NamesKeyAndForeignCount_ThenGoesSilentWhenResolutionStops()
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        SaveEmbeddings(root, provider: "openai", model: "vis-112t4", dimensions: 42);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Calc.cs"),
            "public class Calc { public int Add(int a, int b) => a + b; public int Sub(int a, int b) => a - b; }");
        (await CliTestProcess.RunAsync(root, "add", ".")).ExitCode.ShouldBe(0);
        (await CliTestProcess.RunAsync(root, "index")).ExitCode.ShouldBe(0);
        PlantForeignVintage(root);

        var checks = (await ModelChecksAsync(root)).ToList();
        var model = checks.Single(c =>
            c.GetProperty("name").GetString() == "embedding model" &&
            c.GetProperty("detail").GetString()!.Contains(UniqueKey, StringComparison.Ordinal));
        model.GetProperty("state").GetString().ShouldBe("info");
        model.GetProperty("detail").GetString()
            .ShouldBe($"{UniqueKey} — 1 chunk(s) carry vectors from another embedding model");

        var text = await CliTestProcess.RunAsync(root, "doctor");
        text.StdOut.ShouldContain($"{UniqueKey} — 1 chunk(s) carry vectors from another embedding model");

        SaveEmbeddings(root, provider: "auto", model: null, dimensions: null, ollamaEndpoint: TestWorkspaceConfig.DeadOllamaEndpoint);

        var after = (await ModelChecksAsync(root)).ToList();
        after.ShouldNotContain(c => c.GetProperty("detail").GetString()!.Contains(UniqueKey, StringComparison.Ordinal));
    }

    private static async Task<IReadOnlyList<JsonElement>> ModelChecksAsync(string root)
    {
        var run = await CliTestProcess.RunAsync(root, "doctor", "--json");
        run.ExitCode.ShouldNotBe(2, $"doctor failed to produce a report: {run.StdErr}");
        using var doc = JsonDocument.Parse(run.StdOut);
        return [.. doc.RootElement.GetProperty("checks").EnumerateArray().Select(element => element.Clone())];
    }

    private static void PlantForeignVintage(string root)
    {
        using var store = new MetaStore(SqueezrPaths.MetaDbPath(root));
        var first = store.ListRepos()
            .SelectMany(repo => store.ListFiles(repo.Id))
            .SelectMany(file => store.ListChunksByFile(file.Id))
            .Select(chunk => chunk.Id)
            .First();
        store.MarkChunksEmbedded([first], "ollama/vis-112t4-other/42");
    }

    private static void SaveEmbeddings(
        string root, string provider, string? model, int? dimensions, string? ollamaEndpoint = null)
    {
        var config = SemanticConfigFile.Load(root);
        SemanticConfigFile.Save(root, config with
        {
            Embeddings = config.Embeddings with
            {
                Provider = provider,
                OpenAiCompatible = model is null
                    ? config.Embeddings.OpenAiCompatible
                    : config.Embeddings.OpenAiCompatible with
                    {
                        Model = model,
                        Dimensions = dimensions!.Value,
                        // nothing answers here: the index pass must not reach out to the real api.openai.com
                        Endpoint = "http://127.0.0.1:1/v1",
                    },
                Ollama = ollamaEndpoint is null
                    ? config.Embeddings.Ollama
                    : config.Embeddings.Ollama with { Endpoint = ollamaEndpoint }
            }
        });
    }

    private const string DoctorRemedyText = "squeezr services up";

    [Fact]
    public async Task Doctor_OnIndexedWorkspaceWithDeadQdrant_ReportsSemanticFailure()
    {
        var root = TestDirs.NewTempDir();
        (await CliTestProcess.RunAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        await File.WriteAllTextAsync(Path.Combine(root, "Calc.cs"), "public class Calc { }");
        (await CliTestProcess.RunAsync(root, "add", ".")).ExitCode.ShouldBe(0);
        TestWorkspaceConfig.UseAutoEmbeddingsWithDeadOllama(root);
        (await CliTestProcess.RunAsync(root, "index")).ExitCode.ShouldBe(0);

        var run = await CliTestProcess.RunAsync(root, "doctor", "--json");

        run.ExitCode.ShouldBe(1, customMessage: $"doctor stderr:{Environment.NewLine}{run.StdErr}{Environment.NewLine}doctor stdout:{Environment.NewLine}{run.StdOut}");
        using var doc = JsonDocument.Parse(run.StdOut);
        var checks = doc.RootElement.GetProperty("checks").EnumerateArray().ToList();
        checks.ShouldContain(c =>
            c.GetProperty("name").GetString() == "semantic search" &&
            c.GetProperty("state").GetString() == "fail");
        checks.ShouldNotContain(c => c.GetProperty("name").GetString() == "embeddings provider");
    }
}
