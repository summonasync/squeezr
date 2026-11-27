using System.Diagnostics;
using System.Text.Json;
using Shouldly;

namespace Squeezr.Tests.Lite;

public class LiteCliEndToEndTests : IDisposable
{
    private readonly string _home = TestDirs.NewTempDir();
    private readonly string _workspace = TestDirs.NewTempDir();

    [Fact]
    public async Task Init_Add_Index_ThenEveryQueryVerb_Answers()
    {
        var (initCode, initOut, initErr) = await RunAsync("init", "--json");
        initCode.ShouldBe(0, initErr);
        initOut.ShouldContain("\"workspaceName\"");

        WriteFile("repo/src/Calc.cs", """
            namespace Demo;

            public class Calc
            {
                /// <summary>Adds numbers.</summary>
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public string Name() => "Calc";
            }
            """);
        WriteFile("repo/src/Runner.cs", """
            namespace Demo;

            public class Runner
            {
                public int Go()
                {
                    return new Calc().Add(1, 2);
                }
            }
            """);
        WriteFile("repo/docs/notes.md", "# Notes\n\nThe Calc type adds numbers.\n");

        (var addCode, _, var addErr) = await RunAsync("add", "repo", "--name", "demo");
        addCode.ShouldBe(0, addErr);

        (var indexCode, var indexOut, var indexErr) = await RunAsync("index", "--json");
        indexCode.ShouldBe(0, indexErr);
        indexOut.ShouldContain("\"files\"");

        (var searchCode, var searchOut, var searchErr) = await RunAsync("search", "Add(int a, int b)", "--json");
        searchCode.ShouldBe(0, searchErr);
        searchOut.ShouldContain("\"source\"");
        searchOut.ShouldNotContain("semantic");

        (var statusCode, var statusOut, var statusErr) = await RunAsync("status", "--json");
        statusCode.ShouldBe(0, statusErr);
        using var status = JsonDocument.Parse(statusOut);
        status.RootElement.GetProperty("semantic").GetString().ShouldBe("unsupported");
        status.RootElement.TryGetProperty("embeddings", out _).ShouldBeFalse();
        status.RootElement.TryGetProperty("qdrant", out _).ShouldBeFalse();

        (var mapCode, var mapOut, var mapErr) = await RunAsync("map", "--json");
        mapCode.ShouldBe(0, mapErr);
        mapOut.ShouldContain("\"lines\"");

        (var symbolCode, var symbolOut, var symbolErr) = await RunAsync("symbol", "Add", "--json");
        symbolCode.ShouldBe(0, symbolErr);
        symbolOut.ShouldContain("\"state\"");

        (var refsCode, var refsOut, var refsErr) = await RunAsync("refs", "Add", "--json");
        refsCode.ShouldBe(0, refsErr);
        refsOut.ShouldContain("\"refs\"");

        (var spanCode, var spanOut, var spanErr) = await RunAsync("span", "src/Calc.cs", "4", "--json");
        spanCode.ShouldBe(0, spanErr);
        spanOut.ShouldContain("\"span\"");

        (var packCode, var packOut, var packErr) = await RunAsync("pack", "Calc", "--json");
        packCode.ShouldBe(0, packErr);
        packOut.ShouldContain("\"callers\"");
    }

    [Fact]
    public async Task Index_WithoutInit_ReportsWorkspaceNotInitialized_WithLiteRemedy()
    {
        var (code, _, err) = await RunAsync("index");

        code.ShouldBe(1);
        err.ShouldContain("sqzrlite init");
    }

    [Fact]
    public async Task FullProfileConfig_IndexRuns_WithToleranceWarningOnStderr()
    {
        (var initCode, _, var initErr) = await RunAsync("init", "--json");
        initCode.ShouldBe(0, initErr);

        WriteFullProfileConfig();
        WriteFile("repo/src/Calc.cs", "namespace Demo;\n\npublic class Calc\n{\n}\n");

        (var addCode, _, var addErr) = await RunAsync("add", "repo", "--name", "demo");
        addCode.ShouldBe(0, addErr);

        var (indexCode, _, indexErr) = await RunAsync("index");

        indexCode.ShouldBe(0, indexErr);
        indexErr.ShouldContain("config section 'embeddings'");
        indexErr.ShouldContain("config section 'qdrant'");
    }

    public void Dispose()
    {
        TryRemove(_home);
        TryRemove(_workspace);
    }

    private async Task<(int Code, string StdOut, string StdErr)> RunAsync(params string[] args)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "sqzrlite.dll");
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(dll);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["SQUEEZR_HOME"] = _home;
        startInfo.Environment["SQUEEZR_CLIENT_HOME"] = TestDirs.SharedClientHomeDir;
        startInfo.Environment["DOTNET_CLI_HOME"] = TestDirs.SharedClientHomeDir;

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private void WriteFullProfileConfig()
    {
        var configPath = Squeezr.Core.Storage.SqueezrPaths.ConfigPath(_workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        File.WriteAllText(configPath, """
            {
              "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
              "indexing": { "ignore": ["**/node_modules/**"], "maxFileSizeKb": 512, "noSemantic": false, "rankExclude": [] },
              "watch": { "enabled": false },
              "embeddings": {
                "provider": "ollama",
                "ollama": { "model": "nomic-embed-text", "endpoint": "http://localhost:11434" }
              },
              "qdrant": { "endpoint": "http://localhost:6334" }
            }
            """);
    }

    private void WriteFile(string relativePath, string content)
    {
        var fullPath = Path.Combine(_workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static void TryRemove(string leaf)
    {
        try
        {
            Directory.Delete(leaf, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
