using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

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

        run.ExitCode.ShouldBe(1);
        using var doc = JsonDocument.Parse(run.StdOut);
        var checks = doc.RootElement.GetProperty("checks").EnumerateArray().ToList();
        checks.ShouldContain(c =>
            c.GetProperty("name").GetString() == "semantic search" &&
            c.GetProperty("state").GetString() == "fail");
        checks.ShouldContain(c =>
            c.GetProperty("name").GetString() == "embeddings provider" &&
            (c.GetProperty("detail").GetString() ?? string.Empty).Contains("ollama unreachable"));
    }
}
