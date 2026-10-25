using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Squeezr.Core.Infra;

namespace Squeezr.Tests.Cli;

public sealed class ServicesCommandTests
{
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");

    // Process.Start resolves the executable against the child's overridden PATH on Unix,
    // so tests that blank the PATH must invoke the dotnet muxer by absolute path.
    private static readonly string DotNet = ResolveDotNetMuxer();

    [Fact]
    public async Task Up_OwnedFlag_RaisesQdrantOnlyOwnedProject()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        var docker = FakeDocker.Install();
        var path = docker.BinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        var result = await RunCliAsync(cwd, home, path, "services", "up", "--owned");

        result.ExitCode.ShouldBe(0);
        var composePath = Path.Combine(home, "infra", "docker-compose.owned.yml");
        File.Exists(composePath).ShouldBeTrue();
        var compose = File.ReadAllText(composePath);
        compose.ShouldContain("qdrant/qdrant:latest");
        compose.ShouldContain("\"6335:6333\"");
        compose.ShouldContain("squeezr-owned-qdrant:/qdrant/storage");
        compose.ShouldNotContain("ollama");
        var calls = docker.ReadCalls();
        calls.Count.ShouldBe(3);
        calls[0].ShouldBe("compose version");
        calls[1].ShouldStartWith("compose -f ");
        calls[1].ShouldContain(" -p squeezr-owned pull");
        calls[2].ShouldContain(" -p squeezr-owned up -d");
        result.StdOut.ShouldContain("Owned Qdrant is up");
        result.StdOut.ShouldContain("http://localhost:6335");
    }

    [Fact]
    public async Task Up_NoDecision_NonTTY_FallsBackToHintWithoutRaising()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        var docker = FakeDocker.Install();
        var path = docker.BinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        var result = await RunCliAsync(cwd, home, path, "services", "up");

        result.ExitCode.ShouldBe(0);
        result.StdErr.ShouldContain("hint:");
        File.Exists(Path.Combine(home, "infra", "docker-compose.owned.yml")).ShouldBeFalse();
        File.Exists(Path.Combine(home, "infra", "docker-compose.yml")).ShouldBeFalse();
        docker.ReadCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Up_Owned_WithoutDocker_ReportsErrorInsteadOfStackTrace()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        var noDockerDir = TestDirs.NewTempDir();

        var result = await RunCliAsync(cwd, home, noDockerDir, "services", "up", "--owned");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("Docker");
        result.StdErr.ShouldNotContain("at Squeezr");
    }

    [Fact]
    public async Task Up_Owned_WhenOwnedComposeBlocked_ReportsComposeFailed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        var docker = FakeDocker.Install();
        var path = docker.BinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var infraDir = Path.Combine(home, "infra");
        Directory.CreateDirectory(infraDir);
        Directory.CreateDirectory(Path.Combine(infraDir, "docker-compose.owned.yml"));

        var result = await RunCliAsync(cwd, home, path, "services", "up", "--owned");

        result.ExitCode.ShouldBe(1);
        result.StdErr.ShouldContain("Failed to write the owned compose file");
    }

    [Fact]
    public async Task Down_LegacyPair_StopsOnlyQdrant_AndAnnouncesOllamaLeft()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        var docker = FakeDocker.Install();
        var path = docker.BinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var infraDir = Path.Combine(home, "infra");
        Directory.CreateDirectory(infraDir);
        File.WriteAllText(Path.Combine(infraDir, "docker-compose.yml"), OwnedComposeFileSeed("squeezr"));

        var result = await RunCliAsync(cwd, home, path, "services", "down");

        result.ExitCode.ShouldBe(0);
        var calls = docker.ReadCalls();
        var downCall = calls.Single(c => c.Contains(" down"));
        downCall.ShouldContain("down qdrant");
        downCall.ShouldNotContain("ollama");
        result.StdOut.ShouldContain("Ollama осталась, модели остались");
    }

    [Fact]
    public async Task Down_OwnedProject_PerformsFullOwnedDown()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        var docker = FakeDocker.Install();
        var path = docker.BinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var infraDir = Path.Combine(home, "infra");
        Directory.CreateDirectory(infraDir);
        File.WriteAllText(Path.Combine(infraDir, "docker-compose.owned.yml"), OwnedComposeFileSeed("squeezr-owned"));

        var result = await RunCliAsync(cwd, home, path, "services", "down");

        result.ExitCode.ShouldBe(0);
        var downCall = docker.ReadCalls().Single(c => c.Contains(" down"));
        downCall.ShouldContain(" -p squeezr-owned down");
    }

    [Fact]
    public async Task Status_Json_NdjsonPsOutput_ComposeV5_ParsesContainerStates()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        const string psNdjson =
            "{\"Command\":\"\\\"./entrypoint.sh\\\"\",\"ID\":\"7d58dd5a8634\",\"Name\":\"squeezr-qdrant-1\"," +
            "\"Service\":\"qdrant\",\"State\":\"running\",\"Status\":\"Up 53 minutes\"}\n" +
            "{\"Command\":\"\\\"/bin/ollama serve\\\"\",\"ID\":\"75eb08d6ded5\",\"Name\":\"squeezr-ollama-1\"," +
            "\"Service\":\"ollama\",\"State\":\"running\",\"Status\":\"Up 53 minutes\"}";
        var docker = FakeDocker.Install(psJson: psNdjson, composeVersion: "v5.5.1");
        var path = docker.BinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var infraDir = Path.Combine(home, "infra");
        Directory.CreateDirectory(infraDir);
        File.WriteAllText(Path.Combine(infraDir, "docker-compose.yml"), OwnedComposeFileSeed("squeezr"));

        var status = await RunCliAsync(cwd, home, path, "services", "status", "--json");

        status.ExitCode.ShouldBe(0);
        using var json = JsonDocument.Parse(status.StdOut.Trim());
        var services = json.RootElement.GetProperty("services");
        services.GetArrayLength().ShouldBe(2);
        services[0].GetProperty("service").GetString().ShouldBe("qdrant");
        services[0].GetProperty("name").GetString().ShouldBe("squeezr-qdrant-1");
        services[0].GetProperty("state").GetString().ShouldBe("running");
        services[1].GetProperty("service").GetString().ShouldBe("ollama");
        json.RootElement.GetProperty("composeVersion").GetString().ShouldBe("v5.5.1 (plugin)");
    }

    private static string OwnedComposeFileSeed(string project) => $"""
        # managed by squeezr — test seed for project {project}
        services:
          qdrant:
            image: qdrant/qdrant:latest
            ports:
              - "6333:6333"
            volumes:
              - seed-volume:/qdrant/storage
            restart: unless-stopped
          ollama:
            image: ollama/ollama:latest
        volumes:
          seed-volume:
        """;

    private static string ResolveDotNetMuxer()
    {
        var muxer = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "dotnet");
        return File.Exists(muxer) ? muxer : "dotnet";
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunCliAsync(
        string workingDir, string squeezrHome, string? pathOverride, params string[] args)
    {
        var startInfo = new ProcessStartInfo(DotNet, $"\"{CliDll}\" {string.Join(" ", args)}")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["SQUEEZR_HOME"] = squeezrHome;
        if (pathOverride is not null)
        {
            startInfo.Environment["PATH"] = pathOverride;
        }

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
