using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

// The 066/T3 crash-matrix finding: a workspace config.json with a literal null section used to kill
// the CLI on ConfigFile.Load with rc=255 and a bare "Error:" line on stdout, breaking the JSON
// error-channel contract. These tests replay that shape through real child processes.
[Collection("ProcessGlobals")]
public sealed class ConfigNullSectionCliTests
{
    private static readonly TimeSpan ServeWindow = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Status_NullEmbeddingsSection_JsonStaysParseable()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        try
        {
            (await CliTestProcess.RunWithHomeAsync(home, root, "init", "--json")).ExitCode.ShouldBe(0);
            Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
            File.WriteAllText(SqueezrPaths.ConfigPath(root), ConfigWithNullEmbeddings);

            var run = await CliTestProcess.RunWithHomeAsync(home, root, "status", "--json");

            run.ExitCode.ShouldBe(0);
            using var document = JsonDocument.Parse(run.StdOut.Trim());
            document.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
            run.StdOut.ShouldNotContain("Error:");
        }
        finally
        {
            Cleanup(home, root);
        }
    }

    [Fact]
    public async Task Serve_NullEmbeddingsSection_DoesNotCrashWithBareErrorOnStdout()
    {
        var home = TestDirs.NewTempDir();
        var root = TestDirs.NewTempDir();
        try
        {
            (await CliTestProcess.RunWithHomeAsync(home, root, "init", "--json")).ExitCode.ShouldBe(0);
            Directory.CreateDirectory(SqueezrPaths.StoreDir(root));
            File.WriteAllText(SqueezrPaths.ConfigPath(root), ConfigWithNullEmbeddings);

            using var serve = StartServe(home, root);
            var deadline = Stopwatch.StartNew();
            while (!serve.HasExited && deadline.Elapsed < ServeWindow)
            {
                await Task.Delay(250);
            }

            if (serve.HasExited)
            {
                serve.ExitCode.ShouldNotBe(255);
            }

            serve.StdOut.ToString().ShouldNotContain("Error:");
            serve.StdOut.ToString().ShouldNotContain("Object reference not set");
        }
        finally
        {
            Cleanup(home, root);
        }
    }

    private static string ConfigWithNullEmbeddings =>
        """
        {
          "embeddings": null,
          "chunking": { "maxTokensPerChunk": 512, "fallbackWindowLines": 60, "fallbackOverlapLines": 15 },
          "indexing": { "ignore": [], "maxFileSizeKb": 512 },
          "qdrant": { "endpoint": "http://localhost:6334" }
        }
        """;

    private static ServeProcess StartServe(string home, string workingDir)
    {
        var cliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");
        var startInfo = new ProcessStartInfo("dotnet", $"\"{cliDll}\" serve --watch")
        {
            WorkingDirectory = workingDir,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["SQUEEZR_HOME"] = home;
        var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new ServeProcess(process, stdout, stderr);
    }

    private sealed class ServeProcess(Process process, StringBuilder stdout, StringBuilder stderr) : IDisposable
    {
        public bool HasExited => process.HasExited;

        public int ExitCode => process.ExitCode;

        public StringBuilder StdOut { get; } = stdout;

        public StringBuilder StdErr { get; } = stderr;

        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.Dispose();
        }
    }

    private static void Cleanup(params string[] dirs)
    {
        foreach (var dir in dirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}
