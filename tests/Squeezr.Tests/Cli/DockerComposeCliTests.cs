using System.ComponentModel;
using Shouldly;
using Squeezr.Cli.Infra;

namespace Squeezr.Tests.Cli;

public sealed class DockerComposeCliTests
{
    [Fact]
    public async Task DetectAsync_DockerPluginAvailable_ReturnsPluginInstallation()
    {
        var runner = new StubProcessRunner(
            ("docker", new ProcessRunResult(0, "Docker Compose version v2.29.7", string.Empty), false));
        var cli = new DockerComposeCli(runner);

        var installation = await cli.DetectAsync(CancellationToken.None);

        installation.Plugin.ShouldBeTrue();
        installation.Executable.ShouldBe("docker");
        installation.Version.ShouldBe(new Version(2, 29, 7));
        runner.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DetectAsync_DockerBinaryMissing_FallsBackToStandalone()
    {
        var runner = new StubProcessRunner(
            ("docker", null, true),
            ("docker-compose", new ProcessRunResult(0, "Docker Compose version v2.20.3", string.Empty), false));
        var cli = new DockerComposeCli(runner);

        var installation = await cli.DetectAsync(CancellationToken.None);

        installation.Plugin.ShouldBeFalse();
        installation.Executable.ShouldBe("docker-compose");
        runner.Calls.Count.ShouldBe(2);
        runner.Calls[0].FileName.ShouldBe("docker");
        runner.Calls[1].FileName.ShouldBe("docker-compose");
    }

    [Fact]
    public async Task DetectAsync_PluginVersionCommandFails_FallsBackToStandalone()
    {
        var runner = new StubProcessRunner(
            ("docker", new ProcessRunResult(1, string.Empty, "unknown command"), false),
            ("docker-compose", new ProcessRunResult(0, "Docker Compose version v2.27.0", string.Empty), false));

        var installation = await new DockerComposeCli(runner).DetectAsync(CancellationToken.None);

        installation.Plugin.ShouldBeFalse();
        installation.Executable.ShouldBe("docker-compose");
    }

    [Fact]
    public async Task DetectAsync_StandaloneV1TextOutput_ParsesVersion()
    {
        var runner = new StubProcessRunner(
            ("docker", null, true),
            ("docker-compose", new ProcessRunResult(0, "docker-compose version 1.29.2, build 5becea4c", string.Empty), false));

        var installation = await new DockerComposeCli(runner).DetectAsync(CancellationToken.None);

        installation.Version.ShouldBe(new Version(1, 29, 2));
    }

    [Fact]
    public async Task DetectAsync_UnparseableVersionOutput_StillDetectsWithNullVersion()
    {
        var runner = new StubProcessRunner(
            ("docker", new ProcessRunResult(0, "unexpected", string.Empty), false));

        var installation = await new DockerComposeCli(runner).DetectAsync(CancellationToken.None);

        installation.Plugin.ShouldBeTrue();
        installation.Version.ShouldBeNull();
    }

    [Fact]
    public async Task DetectAsync_NeitherAvailable_ThrowsWithInstallHint()
    {
        var runner = new StubProcessRunner(
            ("docker", null, true),
            ("docker-compose", null, true));

        var ex = await Should.ThrowAsync<ServicesException>(() => new DockerComposeCli(runner).DetectAsync(CancellationToken.None));

        ex.Code.ShouldBe(ServicesException.DockerUnavailableCode);
        ex.Message.ShouldContain("https://docs.docker.com/engine/install/");
    }

    [Fact]
    public async Task RunAsync_PluginInstallation_BuildsComposePrefixedArguments()
    {
        var runner = new StubProcessRunner(
            ("docker", new ProcessRunResult(0, "Docker Compose version v2.29.7", string.Empty), false),
            ("docker", new ProcessRunResult(0, string.Empty, string.Empty), false));
        var cli = new DockerComposeCli(runner);
        var installation = await cli.DetectAsync(CancellationToken.None);
        var timeout = TimeSpan.FromSeconds(42);

        await cli.RunAsync(installation, "/compose.yml", ["up", "-d"], timeout, CancellationToken.None);

        var call = runner.Calls.Last();
        call.FileName.ShouldBe("docker");
        call.Timeout.ShouldBe(timeout);
        call.Arguments.ShouldBe(["compose", "-f", "/compose.yml", "-p", "squeezr", "up", "-d"]);
    }

    [Fact]
    public async Task RunAsync_StandaloneInstallation_OmitsComposePrefix()
    {
        var runner = new StubProcessRunner(
            ("docker", null, true),
            ("docker-compose", new ProcessRunResult(0, "Docker Compose version v2.27.0", string.Empty), false),
            ("docker-compose", new ProcessRunResult(0, string.Empty, string.Empty), false));
        var cli = new DockerComposeCli(runner);
        var installation = await cli.DetectAsync(CancellationToken.None);

        await cli.RunAsync(installation, "/compose.yml", ["ps", "--format", "json"], DockerComposeCli.StatusTimeout, CancellationToken.None);

        var call = runner.Calls.Last();
        call.FileName.ShouldBe("docker-compose");
        call.Arguments.ShouldBe(["-f", "/compose.yml", "-p", "squeezr", "ps", "--format", "json"]);
    }

    [Fact]
    public async Task RunCheckedAsync_NonZeroExit_ThrowsComposeFailedWithStderrDetail()
    {
        var runner = new StubProcessRunner(
            ("docker", new ProcessRunResult(0, "Docker Compose version v2.29.7", string.Empty), false),
            ("docker", new ProcessRunResult(17, string.Empty, "pull failed"), false));
        var cli = new DockerComposeCli(runner);
        var installation = await cli.DetectAsync(CancellationToken.None);

        var ex = await Should.ThrowAsync<ServicesException>(
            () => cli.RunCheckedAsync(installation, "/compose.yml", ["up", "-d"], DockerComposeCli.UpTimeout, CancellationToken.None));

        ex.Code.ShouldBe(ServicesException.ComposeCommandFailedCode);
        ex.Message.ShouldContain("exit code 17");
        ex.Message.ShouldContain("pull failed");
    }

    [Fact]
    public async Task RunCheckedAsync_ZeroExit_ReturnsResult()
    {
        var runner = new StubProcessRunner(
            ("docker", new ProcessRunResult(0, "Docker Compose version v2.29.7", string.Empty), false),
            ("docker", new ProcessRunResult(0, string.Empty, string.Empty), false));
        var cli = new DockerComposeCli(runner);
        var installation = await cli.DetectAsync(CancellationToken.None);

        var result = await cli.RunCheckedAsync(installation, "/compose.yml", ["up", "-d"], DockerComposeCli.UpTimeout, CancellationToken.None);

        result.ExitCode.ShouldBe(0);
    }

    [Fact]
    public async Task PullAsync_InvokesPullWithoutAnyTimeout()
    {
        var runner = new StubProcessRunner(
            ("docker", new ProcessRunResult(0, "Docker Compose version v2.29.7", string.Empty), false),
            ("docker", new ProcessRunResult(0, string.Empty, string.Empty), false));
        var cli = new DockerComposeCli(runner);
        var installation = await cli.DetectAsync(CancellationToken.None);

        await cli.PullAsync(installation, "/compose.yml", progressHandler: null, CancellationToken.None);

        var call = runner.Calls.Last();
        call.Arguments.ShouldBe(["compose", "-f", "/compose.yml", "-p", "squeezr", "pull", "--policy", "missing"]);
        call.Timeout.ShouldBe(Timeout.InfiniteTimeSpan);
    }

    [Theory]
    [InlineData("2.24.0", true)]
    [InlineData("2.29.7", true)]
    [InlineData("2.23.3", false)]
    [InlineData("2.21.0", false)]
    [InlineData(null, false)]
    public async Task PullAsync_PolicyMissingFlagDependsOnComposeVersion(string? version, bool expectPolicyMissing)
    {
        var runner = new StubProcessRunner(
            ("docker", new ProcessRunResult(0, string.Empty, string.Empty), false));
        var cli = new DockerComposeCli(runner);
        var installation = new DockerComposeInstallation(
            "docker", Plugin: true, version is null ? null : Version.Parse(version));

        await cli.PullAsync(installation, "/compose.yml", progressHandler: null, CancellationToken.None);

        var expected = expectPolicyMissing
            ? new[] { "compose", "-f", "/compose.yml", "-p", "squeezr", "pull", "--policy", "missing" }
            : new[] { "compose", "-f", "/compose.yml", "-p", "squeezr", "pull" };
        runner.Calls.Single().Arguments.ShouldBe(expected);
    }

    [Fact]
    public async Task PullAsync_CancelledDuringPull_PropagatesOperationCanceledWithoutTimeoutWording()
    {
        var cli = new DockerComposeCli(new ThrowingStubProcessRunner(new OperationCanceledException()));
        var installation = new DockerComposeInstallation("docker", Plugin: true, Version: null);

        var ex = await Should.ThrowAsync<OperationCanceledException>(
            () => cli.PullAsync(installation, "/compose.yml", progressHandler: null, CancellationToken.None));

        ex.Message.ShouldNotContain("timed out");
        ex.Message.ShouldNotContain("stalled");
    }

    [Fact]
    public async Task PullAsync_NonZeroExit_ThrowsComposeFailedWithStderrDetail()
    {
        var runner = new StubProcessRunner(
            ("docker", new ProcessRunResult(0, "Docker Compose version v2.29.7", string.Empty), false),
            ("docker", new ProcessRunResult(7, string.Empty, "pull denied"), false));
        var cli = new DockerComposeCli(runner);
        var installation = await cli.DetectAsync(CancellationToken.None);

        var ex = await Should.ThrowAsync<ServicesException>(
            () => cli.PullAsync(installation, "/compose.yml", progressHandler: null, CancellationToken.None));

        ex.Code.ShouldBe(ServicesException.ComposeCommandFailedCode);
        ex.Message.ShouldContain("exit code 7");
        ex.Message.ShouldContain("pull denied");
        ex.Message.ShouldContain("docker compose -f /compose.yml -p squeezr pull");
        ex.Message.ShouldContain("squeezr services up");
    }

    [Fact]
    public void ParseServiceStates_ValidJsonArray_ReturnsStates()
    {
        const string psOutput =
            "[{\"Name\":\"squeezr-qdrant-1\",\"Service\":\"qdrant\",\"State\":\"running\",\"Status\":\"Up 2 minutes\"}," +
            "{\"Name\":\"squeezr-ollama-1\",\"Service\":\"ollama\",\"State\":\"exited\",\"Status\":\"Exit 0\"}]";

        var states = DockerComposeCli.ParseServiceStates(psOutput);

        states.Count.ShouldBe(2);
        states[0].ShouldBe(new ComposeServiceState("qdrant", "squeezr-qdrant-1", "running", "Up 2 minutes"));
        states[1].ShouldBe(new ComposeServiceState("ollama", "squeezr-ollama-1", "exited", "Exit 0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n ")]
    public void ParseServiceStates_EmptyOutput_ReturnsEmptyStates(string psOutput)
    {
        DockerComposeCli.ParseServiceStates(psOutput).ShouldBeEmpty();
    }

    [Fact]
    public void ParseServiceStates_NonJsonOutput_ThrowsComposeFailed()
    {
        var ex = Should.Throw<ServicesException>(() => DockerComposeCli.ParseServiceStates("no configuration file"));

        ex.Code.ShouldBe(ServicesException.ComposeCommandFailedCode);
        ex.Message.ShouldContain("Docker Compose v2.21");
    }

    [Fact]
    public void ParseServiceStates_NonArrayJson_ThrowsComposeFailed()
    {
        var ex = Should.Throw<ServicesException>(() => DockerComposeCli.ParseServiceStates("{}"));

        ex.Code.ShouldBe(ServicesException.ComposeCommandFailedCode);
    }

    private sealed class ThrowingStubProcessRunner : IProcessRunner
    {
        private readonly Exception _exception;

        public ThrowingStubProcessRunner(Exception exception)
        {
            _exception = exception;
        }

        public Task<ProcessRunResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken) =>
            Task.FromException<ProcessRunResult>(_exception);
    }

    private sealed class StubProcessRunner : IProcessRunner
    {
        private readonly List<(string FileName, ProcessRunResult? Result, bool Missing)> _behaviors = [];

        public StubProcessRunner(params (string FileName, ProcessRunResult? Result, bool Missing)[] behaviors)
        {
            _behaviors.AddRange(behaviors);
        }

        public List<ProcessInvocation> Calls { get; } = [];

        public Task<ProcessRunResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
        {
            Calls.Add(invocation);
            var behavior = NextBehavior(invocation.FileName);
            if (behavior.Missing)
            {
                throw new Win32Exception(2, $"{invocation.FileName} not found");
            }

            return Task.FromResult(behavior.Result!);
        }

        private (string FileName, ProcessRunResult? Result, bool Missing) NextBehavior(string fileName)
        {
            var index = _behaviors.FindIndex(b => b.FileName == fileName);
            if (index < 0)
            {
                throw new InvalidOperationException($"No stubbed behavior for '{fileName}'.");
            }

            var behavior = _behaviors[index];
            _behaviors.RemoveAt(index);
            return behavior;
        }
    }
}
