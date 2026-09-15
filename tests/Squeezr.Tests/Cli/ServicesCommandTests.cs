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
    public async Task Up_OutsideWorkspace_GeneratesComposeAndStartsServices()
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
        var composePath = Path.Combine(home, "infra", "docker-compose.yml");
        File.Exists(composePath).ShouldBeTrue();
        ServicesComposeFile.IsSqueezrGenerated(composePath).ShouldBeTrue();
        var compose = File.ReadAllText(composePath);
        compose.ShouldContain("qdrant/qdrant:latest");
        compose.ShouldContain("ollama/ollama:latest");
        var calls = docker.ReadCalls();
        calls.Count.ShouldBe(2);
        calls[0].ShouldBe("compose version");
        calls[1].ShouldStartWith("compose -f ");
        calls[1].ShouldContain(" -p squeezr up -d");
        result.StdErr.ShouldNotContain("not initialized");
        result.StdOut.ShouldContain("Qdrant");
        result.StdOut.ShouldContain("Ollama");
        result.StdOut.ShouldContain("ROCm");
    }

    [Fact]
    public async Task Up_Json_OutsideWorkspace_EmitsSingleLineSummaryJson()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        var docker = FakeDocker.Install();
        var path = docker.BinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");

        var result = await RunCliAsync(cwd, home, path, "services", "up", "--json");

        result.ExitCode.ShouldBe(0);
        result.StdErr.ShouldBe(string.Empty);
        result.StdOut.Trim().Split('\n').Length.ShouldBe(1);
        using var json = JsonDocument.Parse(result.StdOut.Trim());
        json.RootElement.GetProperty("composeFile").GetString().ShouldBe(Path.Combine(home, "infra", "docker-compose.yml"));
        json.RootElement.GetProperty("project").GetString().ShouldBe("squeezr");
        json.RootElement.GetProperty("qdrantEndpoint").GetString().ShouldBe("http://localhost:6333");
        json.RootElement.GetProperty("ollamaEndpoint").GetString().ShouldBe("http://localhost:11434");
        json.RootElement.GetProperty("rocm").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        json.RootElement.GetProperty("composeVersion").GetString().ShouldStartWith("v2.29.2");
    }

    [Fact]
    public async Task Down_WithRunningServices_StopsWithoutRemovingVolumes()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        var docker = FakeDocker.Install();
        var path = docker.BinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var up = await RunCliAsync(cwd, home, path, "services", "up");
        up.ExitCode.ShouldBe(0);

        var down = await RunCliAsync(cwd, home, path, "services", "down");

        down.ExitCode.ShouldBe(0);
        var downCall = docker.ReadCalls().Last();
        downCall.ShouldContain(" -p squeezr down");
        downCall.ShouldNotContain(" -v");
        downCall.ShouldNotContain(" -v ");
        down.StdOut.ShouldContain("Data volumes are kept");
    }

    [Fact]
    public async Task Status_Json_WithoutComposeFile_ReportsEmptyWithoutWorkspaceResolution()
    {
        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();

        var result = await RunCliAsync(cwd, home, pathOverride: null, "services", "status", "--json");

        result.ExitCode.ShouldBe(0);
        result.StdErr.ShouldContain("squeezr services up");
        result.StdErr.ShouldNotContain("not initialized");
        using var json = JsonDocument.Parse(result.StdOut.Trim());
        json.RootElement.GetProperty("composeFile").GetString().ShouldBe(Path.Combine(home, "infra", "docker-compose.yml"));
        json.RootElement.GetProperty("project").GetString().ShouldBe("squeezr");
        json.RootElement.GetProperty("services").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Status_Json_WithRunningServices_ParsesContainerStates()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        const string psJson =
            "[{\"Name\":\"squeezr-qdrant-1\",\"Service\":\"qdrant\",\"State\":\"running\",\"Status\":\"Up 2 minutes\"}," +
            "{\"Name\":\"squeezr-ollama-1\",\"Service\":\"ollama\",\"State\":\"running\",\"Status\":\"Up 2 minutes\"}]";
        var docker = FakeDocker.Install(psJson);
        var path = docker.BinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var up = await RunCliAsync(cwd, home, path, "services", "up");
        up.ExitCode.ShouldBe(0);

        var status = await RunCliAsync(cwd, home, path, "services", "status", "--json");

        status.ExitCode.ShouldBe(0);
        using var json = JsonDocument.Parse(status.StdOut.Trim());
        var services = json.RootElement.GetProperty("services");
        services.GetArrayLength().ShouldBe(2);
        services[0].GetProperty("service").GetString().ShouldBe("qdrant");
        services[0].GetProperty("name").GetString().ShouldBe("squeezr-qdrant-1");
        services[0].GetProperty("state").GetString().ShouldBe("running");
        services[1].GetProperty("service").GetString().ShouldBe("ollama");
        json.RootElement.GetProperty("composeVersion").GetString().ShouldBe("v2.29.2 (plugin)");
        var composeHasDevices = File.ReadAllText(Path.Combine(home, "infra", "docker-compose.yml"))
            .Contains("/dev/kfd", StringComparison.Ordinal);
        json.RootElement.GetProperty("rocm").GetBoolean().ShouldBe(composeHasDevices);
    }

    [Fact]
    public async Task Up_WithoutDocker_PrintsInstallHintInsteadOfStackTrace()
    {
        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();

        var result = await RunCliAsync(cwd, home, pathOverride: string.Empty, "services", "up");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldBe(string.Empty);
        result.StdErr.ShouldContain("https://docs.docker.com/engine/install/");
        result.StdErr.ShouldNotContain("not initialized");
        result.StdErr.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task Up_WhenComposeFileBlocked_ReportsComposeFailedWithoutStackTrace()
    {
        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(home, "infra"), "a file where the infra directory belongs");

        var result = await RunCliAsync(cwd, home, pathOverride: string.Empty, "services", "up");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldBe(string.Empty);
        result.StdErr.ShouldContain("Failed to write the Squeezr compose file");
        result.StdErr.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task Up_Json_WithoutDocker_EmitsErrorEnvelopeOnStderr()
    {
        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();

        var result = await RunCliAsync(cwd, home, pathOverride: string.Empty, "services", "up", "--json");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldBe(string.Empty);
        using var json = JsonDocument.Parse(result.StdErr.Trim());
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("docker_unavailable");
        error.GetProperty("message").GetString()!.ShouldContain("https://docs.docker.com/engine/install/");
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

    private static string ResolveDotNetMuxer()
    {
        var processPath = Environment.ProcessPath;
        if (processPath is not null &&
            string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.Ordinal))
        {
            return processPath;
        }

        var onPath = Environment.GetEnvironmentVariable("PATH")?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"))
            .FirstOrDefault(File.Exists);
        return onPath ?? "dotnet";
    }
}
