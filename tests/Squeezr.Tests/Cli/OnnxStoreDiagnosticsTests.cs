using System.Text.Json;
using Shouldly;
using Squeezr.Semantic.Embeddings;
using Squeezr.Core.Storage;
using Squeezr.Core.Workspace;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class OnnxStoreDiagnosticsTests : IDisposable
{
    private readonly string _root = TestDirs.NewTempDir();

    // The onnx workspace must not survive into other doctor runs sharing the run-wide
    // central store: doctor reports "embeddings provider" findings for it, and
    // Doctor_OnIndexedWorkspaceWithDeadQdrant pins that name to absent.
    public void Dispose()
    {
        if (!WorkspaceRegistry.TryGetStoreDir(_root, out var storeDir))
        {
            return;
        }

        WorkspaceRegistry.RemoveByRoot(_root);
        try
        {
            Directory.Delete(storeDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Status_OnnxStore_PrintsMigrationPathInsteadOfRawError()
    {
        await InitIndexedOnnxWorkspaceAsync();

        var run = await CliTestProcess.RunAsync(_root, "status");

        run.ExitCode.ShouldBe(0);
        run.StdOut.ShouldContain("ONNX embedding store detected");
        run.StdOut.ShouldContain(OnnxStoreDiagnostic.ReembedCommand);
        run.StdOut.ShouldContain("dropped");
        run.StdOut.ShouldContain("degraded");
        (run.StdOut + run.StdErr).ShouldNotContain("no longer supported");
        run.StdErr.ShouldNotContain("error:");
    }

    [Fact]
    public async Task Status_Json_OnnxStore_CarriesMigrationWarning()
    {
        await InitIndexedOnnxWorkspaceAsync();

        var run = await CliTestProcess.RunAsync(_root, "status", "--json");

        run.ExitCode.ShouldBe(0);
        using var doc = JsonDocument.Parse(run.StdOut);
        doc.RootElement.GetProperty("warnings").EnumerateArray()
            .ShouldContain(w => w.GetString()!.Contains(OnnxStoreDiagnostic.ReembedCommand));
        doc.RootElement.TryGetProperty("embeddingsProvider", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Doctor_OnnxStore_FailsWithMigrationFinding()
    {
        await InitIndexedOnnxWorkspaceAsync();

        var run = await CliTestProcess.RunAsync(_root, "doctor", "--json");

        run.ExitCode.ShouldBe(1);
        using var doc = JsonDocument.Parse(run.StdOut);
        doc.RootElement.GetProperty("checks").EnumerateArray().ToList()
            .ShouldContain(c =>
                c.GetProperty("name").GetString() == "embeddings provider" &&
                c.GetProperty("state").GetString() == "fail" &&
                c.GetProperty("detail").GetString()!.Contains(OnnxStoreDiagnostic.ReembedCommand));
        (run.StdOut + run.StdErr).ShouldNotContain("no longer supported");
    }

    [Fact]
    public async Task Doctor_OnnxStore_TextRender_ShowsPathAndChangesNothing()
    {
        await InitIndexedOnnxWorkspaceAsync();

        var run = await CliTestProcess.RunAsync(_root, "doctor");

        run.ExitCode.ShouldBe(1);
        run.StdOut.ShouldContain("embeddings provider");
        run.StdOut.ShouldContain(OnnxStoreDiagnostic.ReembedCommand);
        run.StdOut.ShouldContain("nothing was changed");
    }

    private async Task InitIndexedOnnxWorkspaceAsync()
    {
        (await CliTestProcess.RunAsync(_root, "init", "--json")).ExitCode.ShouldBe(0);
        await File.WriteAllTextAsync(Path.Combine(_root, "Calc.cs"), "public class Calc { }");
        (await CliTestProcess.RunAsync(_root, "add", ".")).ExitCode.ShouldBe(0);
        TestWorkspaceConfig.UseAutoEmbeddingsWithDeadOllama(_root);
        (await CliTestProcess.RunAsync(_root, "index")).ExitCode.ShouldBe(0);
        TestWorkspaceConfig.UseOnnxProvider(_root);
    }
}
