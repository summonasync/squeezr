using System.ComponentModel;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Shouldly;
using Spectre.Console;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Infra;
using Squeezr.Core.Infra;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

// 069 moved every services CLI surface into RunCoreAsync unit form: probe-first reads the real host
// ports, so spawning the CLI became host-state dependent (a live Ollama on 11434 turns a run into
// the adopt path). The legacy pins - the policy fallback, verbose pull progress, the whole-fleet
// version+pull+up sequence, compose generation, docker_unavailable envelopes, down volume-keeping
// and the status ps parsing - all survive as asserts against a scripted IProcessRunner and fake
// HTTP handlers. Honest-surface rendering has its own pins in ServicesSurfacesHonestyTests.
[Collection("ProcessGlobals")]
public sealed class ServicesCommandTests : IDisposable
{
    private const string VersionOutput = "Docker Compose version v2.39.0";

    private readonly string? _previousEnv;
    private readonly string _home;

    public ServicesCommandTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable("SQUEEZR_HOME");
        _home = TestDirs.NewTempDir();
        Environment.SetEnvironmentVariable("SQUEEZR_HOME", _home);
        SqueezrHome.Reset();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQUEEZR_HOME", _previousEnv);
        SqueezrHome.Reset();
    }

    [Fact]
    public async Task Up_LegacyAllAbsentFlow_GeneratesComposeAndStartsServices()
    {
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

        var (exit, stdout, stderr) = await RunUpAsync(
            new ServicesUpCommand.Settings { Json = true }, runner, RefuseEverything());

        exit.ShouldBe(0);
        var composePath = Path.Combine(_home, "infra", "docker-compose.yml");
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
        var (exit, _, stderr) = await RunUpAsync(
            new ServicesUpCommand.Settings { Verbose = true }, ScriptedPullProgress(), RefuseEverything());

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
        var runner = new ScriptedRunner(new List<ProcessInvocation>(), invocation =>
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

        var (exit, _, stderr) = await RunUpAsync(
            new ServicesUpCommand.Settings { Json = true }, runner, RefuseEverything());

        exit.ShouldBe(0);
        stderr.ToString().ShouldNotContain("PULL-QDRANT-PROGRESS");
        stderr.ToString().ShouldContain("Pulling images");
    }

    [Fact]
    public async Task Up_Json_EmitsSingleLineSummaryJson()
    {
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

        var (exit, stdout, stderr) = await RunUpAsync(
            new ServicesUpCommand.Settings { Json = true }, runner, RefuseEverything());

        exit.ShouldBe(0);
        stderr.ToString().ShouldContain("Pulling images");
        stderr.ToString().ShouldContain("Starting containers");
        var json = JsonDocument.Parse(stdout.ToString().Trim());
        json.RootElement.GetProperty("composeFile").GetString()
            .ShouldBe(Path.Combine(_home, "infra", "docker-compose.yml"));
        json.RootElement.GetProperty("project").GetString().ShouldBe("squeezr");
        json.RootElement.GetProperty("qdrantEndpoint").GetString().ShouldBe("http://localhost:6334");
        json.RootElement.GetProperty("qdrantRestEndpoint").GetString().ShouldBe("http://localhost:6333");
        json.RootElement.GetProperty("ollamaEndpoint").GetString().ShouldBe("http://localhost:11434");
        json.RootElement.GetProperty("rocm").ValueKind.ShouldBeOneOf(JsonValueKind.True, JsonValueKind.False);
        json.RootElement.GetProperty("composeVersion").GetString().ShouldStartWith("v2.39");
        json.RootElement.GetProperty("services").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Up_OfflineWithLocalImages_PullsOnlyMissingImagesAndStarts()
    {
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

        var (exit, stdout, _) = await RunUpAsync(
            new ServicesUpCommand.Settings { Json = true }, runner, RefuseEverything());

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

        var (exit, _, stderr) = await RunUpAsync(
            new ServicesUpCommand.Settings(), runner, RefuseEverything());

        exit.ShouldBe(0);
        stderr.ToString().ShouldContain("'pull --policy missing'");
        var pull = invocations.Single(i => i.Arguments.Contains("pull"));
        pull.Arguments.ShouldContain("pull");
        pull.Arguments.ShouldNotContain("--policy");
    }

    [Fact]
    public async Task Up_WithoutDocker_PrintsInstallHintInsteadOfStackTrace()
    {
        var runner = new ScriptedRunner([], _ => throw new Win32Exception());

        var (exit, stdout, stderr) = await RunUpAsync(
            new ServicesUpCommand.Settings { Managed = true }, runner, RefuseEverything());

        exit.ShouldBe(1);
        stdout.ToString().ShouldBe(string.Empty);
        stderr.ToString().ShouldContain("https://docs.docker.com/engine/install/");
        stderr.ToString().ShouldNotContain("not initialized");
        stderr.ToString().ShouldNotContain("   at ");
    }

    [Fact]
    public async Task Up_WhenComposeFileBlocked_ReportsComposeFailedWithoutStackTrace()
    {
        File.WriteAllText(Path.Combine(_home, "infra"), "a file where the infra directory belongs");
        var runner = new ScriptedRunner(new List<ProcessInvocation>(), _ => throw new Win32Exception());

        var (exit, stdout, stderr) = await RunUpAsync(
            new ServicesUpCommand.Settings { Managed = true }, runner, RefuseEverything());

        exit.ShouldBe(1);
        stdout.ToString().ShouldBe(string.Empty);
        stderr.ToString().ShouldContain("Failed to write the Squeezr compose file");
        stderr.ToString().ShouldNotContain("   at ");
    }

    [Fact]
    public async Task Up_Json_WithoutDocker_EmitsErrorEnvelopeOnStderr()
    {
        var runner = new ScriptedRunner(new List<ProcessInvocation>(), _ => throw new Win32Exception());

        var (exit, stdout, stderr) = await RunUpAsync(
            new ServicesUpCommand.Settings { Json = true, Managed = true }, runner, RefuseEverything());

        exit.ShouldBe(1);
        using var error = JsonDocument.Parse(stderr.ToString().Trim());
        error.RootElement.GetProperty("error").GetProperty("code").GetString().ShouldBe("docker_unavailable");
        error.RootElement.GetProperty("error").GetProperty("message").GetString()!
            .ShouldContain("https://docs.docker.com/engine/install/");
        using var report = JsonDocument.Parse(stdout.ToString().Trim());
        report.RootElement.GetProperty("project").GetString().ShouldBe("squeezr");
        report.RootElement.GetProperty("services").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Down_WithRunningServices_StopsWithoutRemovingVolumes()
    {
        ServicesComposeFile.Write(false);
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation => invocation.Arguments.Contains("version")
            ? new ProcessRunResult(0, VersionOutput, string.Empty)
            : new ProcessRunResult(0, string.Empty, string.Empty));

        var (exit, stdout, _) = await RunDownAsync(
            new ServicesDownCommand.Settings(), runner, RefuseEverything());

        exit.ShouldBe(0);
        var downCall = invocations.Single(i => i.Arguments.Contains("down"));
        downCall.Arguments.ShouldContain("-p");
        downCall.Arguments.ShouldContain("squeezr");
        downCall.Arguments.ShouldNotContain("-v");
        stdout.ToString().ShouldContain("Data volumes are kept");
        stdout.ToString().ShouldNotContain("still answering");
    }

    [Fact]
    public async Task Down_LiveExternalEndpoints_ReportsThemAsLeftUntouched()
    {
        ServicesComposeFile.Write(false);
        var runner = new ScriptedRunner(new List<ProcessInvocation>(), invocation => invocation.Arguments.Contains("version")
            ? new ProcessRunResult(0, VersionOutput, string.Empty)
            : new ProcessRunResult(0, string.Empty, string.Empty));

        var (exit, stdout, _) = await RunDownAsync(
            new ServicesDownCommand.Settings(), runner, AnswerEverything());

        exit.ShouldBe(0);
        stdout.ToString().ShouldContain("ollama is still answering on");
        stdout.ToString().ShouldContain("qdrant is still answering on");
        stdout.ToString().ShouldContain("left untouched");
    }

    [Fact]
    public async Task Down_Json_EmitsStoppedReport()
    {
        var composePath = ServicesComposeFile.Write(false);
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation => invocation.Arguments.Contains("version")
            ? new ProcessRunResult(0, VersionOutput, string.Empty)
            : new ProcessRunResult(0, string.Empty, string.Empty));

        var (exit, stdout, _) = await RunDownAsync(
            new ServicesDownCommand.Settings { Json = true }, runner, RefuseEverything());

        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(stdout.ToString().Trim());
        json.RootElement.GetProperty("composeFile").GetString().ShouldBe(composePath);
        json.RootElement.GetProperty("project").GetString().ShouldBe("squeezr");
        json.RootElement.GetProperty("stopped").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Down_Json_WithoutComposeFile_ReportsNothingStopped()
    {
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, _ => throw new Win32Exception());

        var (exit, stdout, stderr) = await RunDownAsync(
            new ServicesDownCommand.Settings { Json = true }, runner, RefuseEverything());

        exit.ShouldBe(0);
        stderr.ToString().ShouldContain("no Squeezr compose file found");
        stderr.ToString().ShouldContain("squeezr services up");
        using var json = JsonDocument.Parse(stdout.ToString().Trim());
        json.RootElement.GetProperty("stopped").GetBoolean().ShouldBeFalse();
        invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Status_Json_WithoutComposeFile_ReportsEmptyWithoutWorkspaceResolution()
    {
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, _ => throw new Win32Exception());

        var (exit, stdout, stderr) = await RunStatusAsync(
            new ServicesStatusCommand.Settings { Json = true }, runner, RefuseEverything(), new NoAttributionSource());

        exit.ShouldBe(0);
        stderr.ToString().ShouldContain("squeezr services up");
        stderr.ToString().ShouldNotContain("not initialized");
        using var json = JsonDocument.Parse(stdout.ToString().Trim());
        json.RootElement.GetProperty("composeFile").GetString().ShouldBe(ServicesComposeFile.ComposeFilePath);
        json.RootElement.GetProperty("project").GetString().ShouldBe("squeezr");
        json.RootElement.GetProperty("services").GetArrayLength().ShouldBe(0);
        invocations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Status_Json_WithRunningServices_ParsesContainerStates()
    {
        var composePath = ServicesComposeFile.Write(rocmDevicesAvailable: true);
        const string psJson =
            "[{\"Name\":\"squeezr-qdrant-1\",\"Service\":\"qdrant\",\"State\":\"running\",\"Status\":\"Up 2 minutes\"}," +
            "{\"Name\":\"squeezr-ollama-1\",\"Service\":\"ollama\",\"State\":\"running\",\"Status\":\"Up 2 minutes\"}]";
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation => invocation.Arguments.Contains("ps")
            ? new ProcessRunResult(0, psJson, string.Empty)
            : invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, "Docker Compose version v2.29.2", string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty));

        var (exit, stdout, _) = await RunStatusAsync(
            new ServicesStatusCommand.Settings { Json = true }, runner, RefuseEverything(), new NoAttributionSource());

        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(stdout.ToString().Trim());
        var services = json.RootElement.GetProperty("services");
        services.GetArrayLength().ShouldBe(2);
        services[0].GetProperty("service").GetString().ShouldBe("qdrant");
        services[0].GetProperty("name").GetString().ShouldBe("squeezr-qdrant-1");
        services[0].GetProperty("state").GetString().ShouldBe("running");
        services[1].GetProperty("service").GetString().ShouldBe("ollama");
        json.RootElement.GetProperty("composeVersion").GetString().ShouldBe("v2.29.2 (plugin)");
        File.ReadAllText(composePath).ShouldContain(RocmDeviceProbe.KfdDevicePath);
        json.RootElement.GetProperty("rocm").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Status_Json_NdjsonPsOutput_ComposeV5_ParsesContainerStates()
    {
        ServicesComposeFile.Write(false);
        const string psNdjson =
            "{\"Command\":\"\\\"./entrypoint.sh\\\"\",\"ID\":\"7d58dd5a8634\",\"Name\":\"squeezr-qdrant-1\"," +
            "\"Service\":\"qdrant\",\"State\":\"running\",\"Status\":\"Up 53 minutes\"}\n" +
            "{\"Command\":\"\\\"/bin/ollama serve\\\"\",\"ID\":\"75eb08d6ded5\",\"Name\":\"squeezr-ollama-1\"," +
            "\"Service\":\"ollama\",\"State\":\"running\",\"Status\":\"Up 53 minutes\"}";
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation => invocation.Arguments.Contains("ps")
            ? new ProcessRunResult(0, psNdjson, string.Empty)
            : invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, "Docker Compose version v5.5.1", string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty));

        var (exit, stdout, _) = await RunStatusAsync(
            new ServicesStatusCommand.Settings { Json = true }, runner, RefuseEverything(), new NoAttributionSource());

        exit.ShouldBe(0);
        using var json = JsonDocument.Parse(stdout.ToString().Trim());
        var services = json.RootElement.GetProperty("services");
        services.GetArrayLength().ShouldBe(2);
        services[0].GetProperty("service").GetString().ShouldBe("qdrant");
        services[0].GetProperty("name").GetString().ShouldBe("squeezr-qdrant-1");
        services[0].GetProperty("state").GetString().ShouldBe("running");
        services[1].GetProperty("service").GetString().ShouldBe("ollama");
        json.RootElement.GetProperty("composeVersion").GetString().ShouldBe("v5.5.1 (plugin)");
    }

    private static Task<(int Exit, StringWriter StdOut, StringWriter StdErr)> RunUpAsync(
        ServicesUpCommand.Settings settings, IProcessRunner runner, ServiceLivenessProbe liveness) =>
        RunUnitAsync(ct => ServicesUpCommand.RunCoreAsync(
            settings, new DockerComposeCli(runner), runner, null, null, liveness, ct));

    private static Task<(int Exit, StringWriter StdOut, StringWriter StdErr)> RunDownAsync(
        ServicesDownCommand.Settings settings, IProcessRunner runner, ServiceLivenessProbe liveness) =>
        RunUnitAsync(ct => ServicesDownCommand.RunCoreAsync(settings, runner, liveness, ct));

    private static Task<(int Exit, StringWriter StdOut, StringWriter StdErr)> RunStatusAsync(
        ServicesStatusCommand.Settings settings,
        IProcessRunner runner,
        ServiceLivenessProbe liveness,
        IServiceOwnershipSource ownership) =>
        RunUnitAsync(ct => ServicesStatusCommand.RunCoreAsync(settings, runner, liveness, ownership, ct));

    private static async Task<(int Exit, StringWriter StdOut, StringWriter StdErr)> RunUnitAsync(
        Func<CancellationToken, Task<int>> run)
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
            var exit = await run(CancellationToken.None);
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

    private static ServiceLivenessProbe RefuseEverything() =>
        new(new RefuseHandler());

    private static ServiceLivenessProbe AnswerEverything() =>
        new(new AnswerHandler());

    private static string RunningPs(params string[] services) =>
        $$"""[{{string.Join(",", services.Select(service => $$"""{"Service":"{{service}}","Name":"{{service}}-1","State":"running","Status":"Up 2 minutes"}"""))}}]""";

    private sealed class RefuseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(
                "Connection refused", new SocketException((int)SocketError.ConnectionRefused));
    }

    private sealed class AnswerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
    }

    private sealed class NoAttributionSource : IServiceOwnershipSource
    {
        public Task<ServiceOwnership> AttributeAsync(
            ManagedServiceProbe probe, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("attribution must not be consulted for a refused endpoint");
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
