using System.Text.Json;
using Shouldly;
using Squeezr.Core.Configuration;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

[Collection("ProcessGlobals")]
public sealed class IndexEventsTests
{
    [Fact]
    public async Task Index_Events_FirstRun_EmitsStartedFilesCompletedWithPhaseProgress()
    {
        var root = TestDirs.NewTempDir();
        await InitAddAsync(root, ("a.cs", "class Alpha { }"));

        var index = await RunCliAsync(root, "index", "--events");
        index.ExitCode.ShouldBe(0);

        var types = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseType)
            .ToList();
        types.ShouldBe(["started", "fileIndexed", "progress", "progress", "completed"]);

        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using (var started = JsonDocument.Parse(lines[0]))
        {
            started.RootElement.GetProperty("workspace").GetString().ShouldBe(Path.GetFullPath(root));
        }

        using (var file = JsonDocument.Parse(lines[1]))
        {
            file.RootElement.GetProperty("repo").GetString().ShouldBe("code");
            file.RootElement.GetProperty("path").GetString().ShouldBe("a.cs");
            file.RootElement.GetProperty("files").GetInt32().ShouldBe(1);
        }

        using (var semantic = JsonDocument.Parse(lines[2]))
        {
            semantic.RootElement.GetProperty("phase").GetString().ShouldBe("semantic");
            semantic.RootElement.GetProperty("total").GetInt32().ShouldBe(1);
        }

        using (var completed = JsonDocument.Parse(lines[4]))
        {
            completed.RootElement.GetProperty("files").GetInt32().ShouldBe(1);
            completed.RootElement.GetProperty("added").GetInt32().ShouldBe(1);
            completed.RootElement.GetProperty("symbols").GetInt32().ShouldBeGreaterThan(0);
            completed.RootElement.GetProperty("repos")[0].GetProperty("name").GetString().ShouldBe("code");
        }
    }

    [Fact]
    public async Task Index_Events_QuietIncrementalRun_EmitsExactlyStartedFilesCompleted()
    {
        var root = TestDirs.NewTempDir();
        await InitAddAsync(root,
            ("a.txt", "alpha body"),
            ("b.txt", "beta body"),
            ("c.txt", "gamma body"));
        (await RunCliAsync(root, "index")).ExitCode.ShouldBe(0);

        var index = await RunCliAsync(root, "index", "--events");
        index.ExitCode.ShouldBe(0);

        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(5);

        using (var started = JsonDocument.Parse(lines[0]))
        {
            started.RootElement.GetProperty("type").GetString().ShouldBe("started");
        }

        var expectedPaths = new[] { "a.txt", "b.txt", "c.txt" };
        for (var i = 0; i < expectedPaths.Length; i++)
        {
            using var doc = JsonDocument.Parse(lines[1 + i]);
            var e = doc.RootElement;
            e.GetProperty("type").GetString().ShouldBe("fileIndexed");
            e.GetProperty("repo").GetString().ShouldBe("code");
            e.GetProperty("path").GetString().ShouldBe(expectedPaths[i]);
            e.GetProperty("files").GetInt32().ShouldBe(i + 1);
        }

        using (var completed = JsonDocument.Parse(lines[4]))
        {
            var e = completed.RootElement;
            e.GetProperty("type").GetString().ShouldBe("completed");
            e.GetProperty("durationMs").GetInt32().ShouldBeGreaterThanOrEqualTo(0);
            e.GetProperty("files").GetInt32().ShouldBe(3);
            e.GetProperty("added").GetInt32().ShouldBe(0);
            e.GetProperty("modified").GetInt32().ShouldBe(0);
            e.GetProperty("repos")[0].GetProperty("name").GetString().ShouldBe("code");
        }
    }

    [Fact]
    public async Task Index_Events_ComposesWithJson_AppendingFinalResultObject()
    {
        var root = TestDirs.NewTempDir();
        await InitAddAsync(root, ("a.txt", "alpha body"));
        (await RunCliAsync(root, "index")).ExitCode.ShouldBe(0);

        var index = await RunCliAsync(root, "index", "--events", "--json");
        index.ExitCode.ShouldBe(0);

        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(4);
        ParseType(lines[0]).ShouldBe("started");
        ParseType(lines[1]).ShouldBe("fileIndexed");
        ParseType(lines[2]).ShouldBe("completed");

        using var final = JsonDocument.Parse(lines[3]);
        final.RootElement.TryGetProperty("type", out _).ShouldBeFalse();
        final.RootElement.GetProperty("repos")[0].GetProperty("files").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task Index_Events_WithoutWorkspace_EmitsTerminalErrorEventAndStderrEnvelope()
    {
        var root = TestDirs.NewTempDir();

        var index = await RunCliAsync(root, "index", "--events");
        index.ExitCode.ShouldBe(1);

        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(2);
        using (var started = JsonDocument.Parse(lines[0]))
        {
            started.RootElement.GetProperty("type").GetString().ShouldBe("started");
        }

        using (var error = JsonDocument.Parse(lines[1]))
        {
            error.RootElement.GetProperty("type").GetString().ShouldBe("error");
            error.RootElement.GetProperty("code").GetString().ShouldBe("workspace_not_initialized");
        }

        var envelope = ParseStdErrError(index.StdErr);
        envelope.GetProperty("code").GetString().ShouldBe("workspace_not_initialized");
    }

    [Fact]
    public async Task Index_WithoutEvents_KeepsPlainHumanOutput()
    {
        var root = TestDirs.NewTempDir();
        await InitAddAsync(root, ("a.txt", "alpha body"));
        (await RunCliAsync(root, "index")).ExitCode.ShouldBe(0);

        var index = await RunCliAsync(root, "index");
        index.ExitCode.ShouldBe(0);
        index.StdOut.ShouldContain("code: +0 ~0 -0 (skipped: 0, files: 1)");
        index.StdOut.TrimStart().StartsWith('{').ShouldBeFalse();
    }

    [Fact]
    public async Task Index_Events_AutoProviderWithDeadOllama_FallsBackToOnnxAndCarriesProvider()
    {
        var root = TestDirs.NewTempDir();
        await InitAddAsync(root, ("a.txt", "alpha body"));
        TestWorkspaceConfig.UseAutoEmbeddingsWithDeadOllama(root);

        var index = await RunCliAsync(root, "index", "--events");

        index.ExitCode.ShouldBe(0);
        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using (var started = JsonDocument.Parse(lines[0]))
        {
            started.RootElement.GetProperty("type").GetString().ShouldBe("started");
            started.RootElement.GetProperty("provider").GetString().ShouldBe("onnx");
        }

        using (var completed = JsonDocument.Parse(FindLine(lines, "completed")))
        {
            completed.RootElement.GetProperty("type").GetString().ShouldBe("completed");
            completed.RootElement.GetProperty("provider").GetString().ShouldBe("onnx");
        }

        index.StdErr.ShouldContain("embeddings provider: onnx (ollama unreachable:");
    }

    [Fact]
    public async Task Index_NoSemanticFlag_SkipsProviderResolutionEntirely()
    {
        var root = TestDirs.NewTempDir();
        await InitAddAsync(root, ("a.txt", "alpha body"));
        TestWorkspaceConfig.UseAutoEmbeddingsWithDeadOllama(root);

        var index = await RunCliAsync(root, "index", "--no-semantic", "--events");

        index.ExitCode.ShouldBe(0);
        index.StdErr.ShouldNotContain("embeddings provider:");
        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using (var started = JsonDocument.Parse(lines[0]))
        {
            started.RootElement.GetProperty("type").GetString().ShouldBe("started");
            started.RootElement.TryGetProperty("provider", out _).ShouldBeFalse();
        }

        ParseType(lines[^1]).ShouldBe("completed");
    }

    [Fact]
    public async Task Index_NoSemanticFromConfig_SkipsProviderResolutionEntirely()
    {
        var root = TestDirs.NewTempDir();
        await InitAddAsync(root, ("a.txt", "alpha body"));
        TestWorkspaceConfig.UseAutoEmbeddingsWithDeadOllama(root);
        var config = ConfigFile.Load(root);
        ConfigFile.Save(root, config with { Indexing = config.Indexing with { NoSemantic = true } });

        var index = await RunCliAsync(root, "index", "--events");

        index.ExitCode.ShouldBe(0);
        index.StdErr.ShouldNotContain("embeddings provider:");
        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using (var started = JsonDocument.Parse(lines[0]))
        {
            started.RootElement.GetProperty("type").GetString().ShouldBe("started");
            started.RootElement.TryGetProperty("provider", out _).ShouldBeFalse();
        }

        ParseType(lines[^1]).ShouldBe("completed");
    }

    [Fact]
    public async Task Index_Events_RegistryBusy_EmitsSingleErrorEventWithBusyCodeAndNoStarted()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        await InitAddWithHomeAsync(home, root, ("a.txt", "alpha body"));

        using (LockRegistry(home))
        {
            var index = await CliTestProcess.RunWithHomeAsync(home, root, "index", "--events");

            index.ExitCode.ShouldBe(1);
            var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            lines.Length.ShouldBe(1);
            using var error = JsonDocument.Parse(lines[0]);
            error.RootElement.GetProperty("type").GetString().ShouldBe("error");
            error.RootElement.GetProperty("code").GetString().ShouldBe("workspace_registry_busy");
            ParseStdErrError(index.StdErr).GetProperty("code").GetString().ShouldBe("workspace_registry_busy");
        }
    }

    [Fact]
    public async Task Index_ReembedWithNoSemantic_WarnsOnStderrAndIgnoresReembed()
    {
        var root = TestDirs.NewTempDir();
        await InitAddAsync(root, ("a.txt", "alpha body"));

        var index = await RunCliAsync(root, "index", "--reembed", "--no-semantic", "--events");

        index.ExitCode.ShouldBe(0);
        index.StdErr.ShouldContain("warning: --reembed requires semantic indexing; ignored");
        var lines = index.StdOut.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        ParseType(lines[0]).ShouldBe("started");
        ParseType(lines[^1]).ShouldBe("completed");
    }

    private static FileStream LockRegistry(string home) =>
        new(
            Path.Combine(home, SqueezrPaths.RegistryFileName),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

    private static async Task InitAddWithHomeAsync(
        string home, string root, params (string Path, string Content)[] files)
    {
        (await CliTestProcess.RunWithHomeAsync(home, root, "init", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        foreach (var (path, content) in files)
        {
            File.WriteAllText(Path.Combine(root, "code", path), content);
        }

        (await CliTestProcess.RunWithHomeAsync(home, root, "add", "code", "--name", "code", "--json"))
            .ExitCode.ShouldBe(0);
    }

    private static string ParseType(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("type").GetString()!;
    }

    private static string FindLine(string[] lines, string type)
    {
        foreach (var line in lines)
        {
            if (ParseType(line) == type)
            {
                return line;
            }
        }

        throw new InvalidOperationException($"No '{type}' event in the stream.");
    }

    private static JsonElement ParseStdErrError(string stdErr)
    {
        var lines = stdErr.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using var doc = JsonDocument.Parse(lines[^1]);
        return doc.RootElement.GetProperty("error").Clone();
    }

    private static async Task InitAddAsync(string root, params (string Path, string Content)[] files)
    {
        (await RunCliAsync(root, "init", "--json")).ExitCode.ShouldBe(0);
        Directory.CreateDirectory(Path.Combine(root, "code"));
        foreach (var (path, content) in files)
        {
            File.WriteAllText(Path.Combine(root, "code", path), content);
        }

        (await RunCliAsync(root, "add", "code", "--name", "code", "--json")).ExitCode.ShouldBe(0);
    }

    private static Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, params string[] args) => CliTestProcess.RunAsync(workingDir, args);
}
