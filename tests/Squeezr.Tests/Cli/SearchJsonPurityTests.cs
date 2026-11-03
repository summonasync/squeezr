using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class SearchJsonPurityTests
{
    [Fact]
    public async Task Search_JsonOnModelLessBox_StderrIsOneWarningsEnvelope()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--name", "purity", "--json")).ExitCode.ShouldBe(0);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "alpha body");
        (await RunCliAsync(root, "add", ".")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index")).ExitCode.ShouldBe(0);
        TestWorkspaceConfig.UseAutoEmbeddingsWithDeadOllama(root);

        var search = await RunCliAsync(root, "search", "alpha", "--json");

        search.ExitCode.ShouldBe(0);
        search.StdOut.ShouldContain("\"summary\"");
        var stderr = search.StdErr.Trim();
        stderr.ShouldStartWith("{");
        using var doc = JsonDocument.Parse(stderr);
        var warnings = doc.RootElement.GetProperty("warnings");
        warnings.GetArrayLength().ShouldBe(1);
        warnings[0].GetProperty("code").GetString().ShouldBe("embeddings_unavailable");
        warnings[0].GetProperty("message").GetString().ShouldContain("ollama probe failed");
        warnings[0].GetProperty("message").GetString().ShouldContain("'ollama' or 'openai'");
    }

    [Fact]
    public async Task Search_JsonErrorPath_SuppressesProviderWarnings()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--name", "purity-err", "--json")).ExitCode.ShouldBe(0);
        TestWorkspaceConfig.UseAutoEmbeddingsWithDeadOllama(root);

        var search = await RunCliAsync(root, "search", "anything", "--json");

        search.ExitCode.ShouldBe(1);
        var stderr = search.StdErr.Trim();
        stderr.ShouldStartWith("{");
        using var doc = JsonDocument.Parse(stderr);
        doc.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("index_not_built");
        doc.RootElement.TryGetProperty("warnings", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Search_PlainMode_StillShowsProviderWarning()
    {
        var root = TestDirs.NewTempDir();
        (await RunCliAsync(root, "init", "--name", "purity-plain", "--json")).ExitCode.ShouldBe(0);
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "alpha body");
        (await RunCliAsync(root, "add", ".")).ExitCode.ShouldBe(0);
        (await RunCliAsync(root, "index")).ExitCode.ShouldBe(0);
        TestWorkspaceConfig.UseAutoEmbeddingsWithDeadOllama(root);

        var search = await RunCliAsync(root, "search", "alpha");

        search.ExitCode.ShouldBe(0);
        search.StdErr.ShouldContain("warning: ollama probe failed");
        search.StdErr.ShouldContain("Set embeddings.provider to 'ollama' or 'openai'");
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
