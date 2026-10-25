using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Spectre.Console;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Infra;
using Squeezr.Core.Infra;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

// 069 migrated the services up CLI coverage into RunCoreAsync unit form: probe-first reads the real
// host ports, so spawning the CLI with a PATH fake docker became host-state dependent (a live Ollama
// on 11434 turns every test into the adopt path). Legacy pins - the policy fallback, verbose pull
// progress, the whole-fleet version+pull+up call sequence, compose generation, docker_unavailable -
// all survive as asserts against a scripted IProcessRunner and a fake HTTP handler. The down and
// status CLI tests keep their spawn form with --managed: the up preamble must stay deterministic
// on any host (the probe itself still runs safe GETs against the endpoint constants).
public sealed class ServicesCommandTests
{
    private const string VersionOutput = "Docker Compose version v2.39.0";
    private const string AdoptModelName = "nomic-embed-text";
    private static readonly string CliDll = Path.Combine(AppContext.BaseDirectory, "squeezr.dll");
    private static readonly string DotNet = ResolveDotNetMuxer();

    [Fact]
    public async Task Up_LegacyAllAbsentFlow_GeneratesComposeAndStartsServices()
    {
        var home = TestDirs.NewTempDir();
        using var scope = PinHome(home);
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(0, RunningPs("qdrant", "ollama"), string.Empty);
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });

        var (exit, stdout, stderr) = await RunUnitAsync(
            new ServicesUpCommand.Settings { Json = true }, runner, liveness: RefuseEverything());

        exit.ShouldBe(0);
        var composePath = Path.Combine(home, "infra", "docker-compose.yml");
        File.Exists(composePath).ShouldBeTrue();
        ServicesComposeFile.IsSqueezrGenerated(composePath).ShouldBeTrue();
        var compose = File.ReadAllText(composePath);
        compose.ShouldContain("qdrant/qdrant:latest");
        compose.ShouldContain("ollama/ollama:latest");
        invocations.Count(i => i.Arguments.Contains("version")).ShouldBe(2);
        var pulls = invocations.Where(i => i.Arguments.Contains("pull")).ToList();
        pulls.ShouldHaveSingleItem();
        pulls[0].Arguments.ShouldContain("--policy");
        pulls[0].Arguments.ShouldContain("missing");
        pulls[0].Arguments.ShouldContain("-p");
        pulls[0].Arguments.ShouldContain("squeezr");
        var ups = invocations.Where(i => i.Arguments.Contains("up")).ToList();
        ups.ShouldHaveSingleItem();
        ups[0].Arguments.ShouldContain("up");
        ups[0].Arguments.ShouldContain("-d");
        ups[0].Arguments.ShouldNotContain("ollama");
        ups[0].Arguments.ShouldNotContain("qdrant");
        invocations.Count(i => i.Arguments.Contains("ps")).ShouldBeGreaterThanOrEqualTo(1);
        stderr.ToString().ShouldContain("Pulling images");
        stderr.ToString().ShouldContain("Starting containers");
        stderr.ToString().ShouldNotContain("not initialized");
        using var document = JsonDocument.Parse(stdout.ToString().Trim());
        var payload = document.RootElement;
        payload.GetProperty("project").GetString().ShouldBe("squeezr");
        payload.GetProperty("qdrantEndpoint").GetString().ShouldBe("http://localhost:6334");
        payload.GetProperty("qdrantRestEndpoint").GetString().ShouldBe("http://localhost:6333");
        payload.GetProperty("rocm").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        payload.GetProperty("services").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Up_Verbose_StreamsRawPullOutputToStderr()
    {
        var home = TestDirs.NewTempDir();
        using var scope = PinHome(home);
        var invocations = new List<ProcessInvocation>();

        var (exit, _, stderr) = await RunUnitAsync(
            new ServicesUpCommand.Settings { Verbose = true }, ScriptedPullProgress(), liveness: RefuseEverything());

        exit.ShouldBe(0);
        stderr.ToString().ShouldContain("PULL-QDRANT-PROGRESS");
        stderr.ToString().ShouldContain("PULL-OLLAMA-PROGRESS");
    }

    private static IProcessRunner ScriptedPullProgress()
    {
        return new ScriptedRunner(new List<ProcessInvocation>(), invocation =>
        {
            if (invocation.Arguments.Contains("pull"))
            {
                invocation.ProgressHandler?.Invoke("PULL-QDRANT-PROGRESS");
                invocation.ProgressHandler?.Invoke("PULL-OLLAMA-PROGRESS");
            }

            return invocation.Arguments.Contains("ps")
                ? new ProcessRunResult(0, RunningPs("qdrant", "ollama"), string.Empty)
                : invocation.Arguments.Contains("version")
                    ? new ProcessRunResult(0, VersionOutput, string.Empty)
                    : new ProcessRunResult(0, string.Empty, string.Empty);
        });
    }

    [Fact]
    public async Task Up_WithoutVerbose_KeepsRawPullOutputHidden()
    {
        var home = TestDirs.NewTempDir();
        using var scope = PinHome(home);
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.Arguments.Contains("pull"))
            {
                invocation.ProgressHandler?.Invoke("PULL-QDRANT-PROGRESS");
            }

            return invocation.Arguments.Contains("ps")
                ? new ProcessRunResult(0, RunningPs("qdrant", "ollama"), string.Empty)
                : invocation.Arguments.Contains("version")
                    ? new ProcessRunResult(0, VersionOutput, string.Empty)
                    : new ProcessRunResult(0, string.Empty, string.Empty);
        });
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        try
        {
            var (exit, _, stderr) = await RunUnitAsync(
                new ServicesUpCommand.Settings { Json = true }, runner, liveness: RefuseEverything());

            exit.ShouldBe(0);
                        stderr.ToString().ShouldNotContain("PULL-QDRANT-PROGRESS");
            stderr.ToString().ShouldContain("Pulling images");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public async Task Up_Json_EmitsSingleLineSummaryJson()
    {
        var home = TestDirs.NewTempDir();
        using var scope = PinHome(home);
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(0, RunningPs("qdrant", "ollama"), string.Empty);
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        try
        {
            var (exit, stdout, stderr) = await RunUnitAsync(
                new ServicesUpCommand.Settings { Json = true }, runner, liveness: RefuseEverything());

            exit.ShouldBe(0);
            stderr.ToString().ShouldContain("Pulling images");
            stderr.ToString().ShouldContain("Starting containers");
            var json = JsonDocument.Parse(stdout.ToString().Trim());
            json.RootElement.GetProperty("composeFile").GetString()
                .ShouldBe(Path.Combine(home, "infra", "docker-compose.yml"));
            json.RootElement.GetProperty("project").GetString().ShouldBe("squeezr");
            json.RootElement.GetProperty("qdrantEndpoint").GetString().ShouldBe("http://localhost:6334");
            json.RootElement.GetProperty("qdrantRestEndpoint").GetString().ShouldBe("http://localhost:6333");
            json.RootElement.GetProperty("ollamaEndpoint").GetString().ShouldBe("http://localhost:11434");
            json.RootElement.GetProperty("rocm").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
            json.RootElement.GetProperty("composeVersion").GetString().ShouldStartWith("v2.39");
            json.RootElement.GetProperty("services").GetArrayLength().ShouldBe(2);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public async Task Up_OfflineWithLocalImages_PullsOnlyMissingImagesAndStarts()
    {
        var home = TestDirs.NewTempDir();
        using var scope = PinHome(home);
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.Arguments.Contains("pull"))
            {
                return invocation.Arguments.Contains("--policy")
                    ? new ProcessRunResult(0, string.Empty, string.Empty)
                    : new ProcessRunResult(1, string.Empty, "offline");
            }

            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(0, RunningPs("qdrant", "ollama"), string.Empty);
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });

        var (exit, stdout, _) = await RunUnitAsync(
            new ServicesUpCommand.Settings { Json = true }, runner, liveness: RefuseEverything());

        exit.ShouldBe(0);
        var pull = invocations.Single(i => i.Arguments.Contains("pull"));
        pull.Arguments.ShouldContain("--policy");
        pull.Arguments.ShouldContain("missing");
        using var document = JsonDocument.Parse(stdout.ToString().Trim());
        document.RootElement.GetProperty("services").EnumerateArray()
            .Count(item => item.GetProperty("state").GetString() == "running").ShouldBe(2);
    }

    [Fact]
    public async Task Up_OldComposeWithoutPolicySupport_PrintsFallbackNote()
    {
        var home = TestDirs.NewTempDir();
        using var scope = PinHome(home);
        var invocations = new List<ProcessInvocation>();
        const string oldVersion = "Docker Compose version v2.21.0";
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(0, RunningPs("qdrant", "ollama"), string.Empty);
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, oldVersion, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });

        var (exit, _, stderr) = await RunUnitAsync(
            new ServicesUpCommand.Settings(), runner, liveness: RefuseEverything());

        exit.ShouldBe(0);
        stderr.ToString().ShouldContain("'pull --policy missing'");
        var pull = invocations.Single(i => i.Arguments.Contains("pull"));
        pull.Arguments.ShouldContain("pull");
        pull.Arguments.ShouldNotContain("--policy");
    }

    [Fact]
    public async Task Down_WithRunningServices_StopsWithoutRemovingVolumes()
    {
        if (HostFleetIsLive())
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        var docker = FakeDocker.Install();
        var path = docker.BinDir + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        var up = await RunCliAsync(cwd, home, path, "services", "up", "--managed");
        up.ExitCode.ShouldBe(0);

        var down = await RunCliAsync(cwd, home, path, "services", "down");

        down.ExitCode.ShouldBe(0);
        var downCall = docker.ReadCalls().Last();
        downCall.ShouldContain(" -p squeezr down");
        downCall.ShouldNotContain(" -v");
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
        if (HostFleetIsLive())
        {
            return;
        }

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
        var up = await RunCliAsync(cwd, home, path, "services", "up", "--managed");
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
    public async Task Status_Json_NdjsonPsOutput_ComposeV5_ParsesContainerStates()
    {
        if (HostFleetIsLive())
        {
            return;
        }

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
        var up = await RunCliAsync(cwd, home, path, "services", "up", "--managed");
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
        json.RootElement.GetProperty("composeVersion").GetString().ShouldBe("v5.5.1 (plugin)");
    }

    [Fact]
    public async Task Up_WithoutDocker_PrintsInstallHintInsteadOfStackTrace()
    {
        if (HostFleetIsLive())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();

        var result = await RunCliAsync(cwd, home, pathOverride: string.Empty, "services", "up", "--managed");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldBe(string.Empty);
        result.StdErr.ShouldContain("https://docs.docker.com/engine/install/");
        result.StdErr.ShouldNotContain("not initialized");
        result.StdErr.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task Up_WhenComposeFileBlocked_ReportsComposeFailedWithoutStackTrace()
    {
        if (HostFleetIsLive())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();
        File.WriteAllText(Path.Combine(home, "infra"), "a file where the infra directory belongs");

        var result = await RunCliAsync(cwd, home, pathOverride: string.Empty, "services", "up", "--managed");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldBe(string.Empty);
        result.StdErr.ShouldContain("Failed to write the Squeezr compose file");
        result.StdErr.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task Up_Json_WithoutDocker_EmitsErrorEnvelopeOnStderr()
    {
        if (HostFleetIsLive())
        {
            return;
        }

        var cwd = TestDirs.NewTempDir();
        var home = TestDirs.NewTempDir();

        var result = await RunCliAsync(cwd, home, pathOverride: string.Empty, "services", "up", "--managed", "--json");

        result.ExitCode.ShouldBe(1);
        result.StdOut.ShouldBe(string.Empty);
        using var json = JsonDocument.Parse(result.StdErr.Trim());
        var error = json.RootElement.GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("docker_unavailable");
        error.GetProperty("message").GetString()!.ShouldContain("https://docs.docker.com/engine/install/");
    }

    private static async Task<(int Exit, StringWriter StdOut, StringWriter StdErr)> RunUnitAsync(
        ServicesUpCommand.Settings settings,
        IProcessRunner runner,
        ServiceLivenessProbe liveness)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        var originalConsole = AnsiConsole.Console;
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(stdout)
        });
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exit = await ServicesUpCommand.RunCoreAsync(
                settings, new DockerComposeCli(runner), runner, null, null, liveness, CancellationToken.None);
            stdout.Flush();
            stderr.Flush();
            return (exit, stdout, stderr);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
            AnsiConsole.Console = originalConsole;
        }
    }

    private static IDisposable PinHome(string home)
    {
        var previous = Environment.GetEnvironmentVariable("SQUEEZR_HOME");
        Environment.SetEnvironmentVariable("SQUEEZR_HOME", home);
        SqueezrHome.Reset();
        return new HomeRestorer(previous);
    }

    private sealed class HomeRestorer(string? previous) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("SQUEEZR_HOME", previous);
            SqueezrHome.Reset();
        }
    }

    // 069 probe-first: services up now reads the real host ports. On a host running the live fleet
    // (the developer's Ollama and Qdrant) the adopt/refuse paths fire and the legacy spawn-form
    // expectations stop being deterministic - such tests skip instead of guessing host state.
    private static bool HostFleetIsLive()
    {
        foreach (var port in new[] { 11434, 6333 })
        {
            using var client = new TcpClient();
            try
            {
                client.Connect("127.0.0.1", port);
                return true;
            }
            catch (SocketException)
            {
            }
        }

        return false;
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

    private static ServiceLivenessProbe RefuseEverything() =>
        new(new RefuseHandler());

    private static string RunningPs(params string[] services) =>
        $$"""[{{string.Join(",", services.Select(service => $$"""{"Service":"{{service}}","Name":"{{service}}-1","State":"running","Status":"Up 2 minutes"}"""))}}]""";

    private sealed class RefuseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(
                "Connection refused", new SocketException((int)SocketError.ConnectionRefused));
    }

    private sealed class ScriptedRunner : IProcessRunner
    {
        private readonly List<ProcessInvocation> log;
        private readonly Func<ProcessInvocation, ProcessRunResult> script;

        public ScriptedRunner(List<ProcessInvocation> log, Func<ProcessInvocation, ProcessRunResult> script)
        {
            this.log = log;
            this.script = script;
        }

        public Task<ProcessRunResult> RunAsync(
            ProcessInvocation invocation, CancellationToken cancellationToken)
        {
            lock (log)
            {
                log.Add(invocation);
            }

            return Task.FromResult(script(invocation));
        }
    }
}
