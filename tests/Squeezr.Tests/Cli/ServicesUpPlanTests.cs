using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Shouldly;
using Squeezr.Cli.Commands;
using Squeezr.Cli.Infra;
using Squeezr.Core.Infra;
using Squeezr.Core.Storage;

namespace Squeezr.Tests.Cli;

// The execution phase of services up against a scripted process runner, a fake liveness handler and
// fake HTTP for the model ensure - no real docker daemon, no live endpoint. SqueezrHome is pinned
// because the compose file is written into the pinned home's infra directory.
[Collection("ProcessGlobals")]
public sealed class ServicesUpPlanTests : IDisposable
{
    private const string VersionOutput = "Docker Compose version v2.39.0";
    private const string AdoptModelName = "nomic-embed-text";

    private readonly string? _previousEnv;
    private readonly string _home;
    private readonly List<string> _createdDirs = [];

    public ServicesUpPlanTests()
    {
        _previousEnv = Environment.GetEnvironmentVariable("SQUEEZR_HOME");
        _home = TestDirs.NewTempDir();
        _createdDirs.Add(_home);
        Environment.SetEnvironmentVariable("SQUEEZR_HOME", _home);
        SqueezrHome.Reset();
    }

    [Fact]
    public async Task RunCore_AllAbsent_UsesTheLegacyWholeFleetPullAndUp()
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

        var exit = await ServicesUpCommand.RunCoreAsync(
            new ServicesUpCommand.Settings(),
            new DockerComposeCli(runner), runner, null, null, RefuseEverything(), CancellationToken.None);

        exit.ShouldBe(0);
        var pulls = invocations.Where(i => i.Arguments.Contains("pull")).ToList();
        pulls.ShouldHaveSingleItem();
        pulls[0].Arguments.ShouldContain("--policy");
        pulls[0].Arguments.ShouldContain("missing");
        var ups = invocations.Where(i => i.Arguments.Contains("up")).ToList();
        ups.ShouldHaveSingleItem();
        ups[0].Arguments.ShouldNotContain("qdrant");
        ups[0].Arguments.ShouldNotContain("ollama");
        ups[0].Arguments[^1].ShouldBe("-d");
        File.Exists(ServicesComposeFile.ComposeFilePath).ShouldBeTrue();
    }

    [Fact]
    public async Task RunCore_ForeignOllamaLive_AdoptsAndNeverStartsOurs()
    {
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.FileName == "docker" && invocation.Arguments.FirstOrDefault() == "ps")
            {
                return new ProcessRunResult(0, "foreign-ollama|||0.0.0.0:11434->11434/tcp", string.Empty);
            }

            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(0, RunningPs("qdrant"), string.Empty);
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });

        var exit = await ServicesUpCommand.RunCoreAsync(
            new ServicesUpCommand.Settings(),
            new DockerComposeCli(runner), runner, AnswerWithModelHandler(),
            _ => Task.FromResult<string?>("0.5.0"),
            AnswerAlways(),
            CancellationToken.None);

        exit.ShouldBe(0);
        invocations.Where(i => i.Arguments.Contains("pull")).ShouldBeEmpty();
        invocations.Where(i => i.Arguments.Contains("up")).ShouldBeEmpty();
        invocations.ShouldContain(i =>
            i.Arguments.Contains("pull") == false && i.FileName == "docker" &&
            i.Arguments.Contains("ps"));
    }

    [Fact]
    public async Task RunCore_HalfStartedCompose_ReportsEachServiceFinalStateAndFails()
    {
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.Arguments.Contains("up") && invocation.Arguments.Contains("ollama"))
            {
                return new ProcessRunResult(1, string.Empty, "port is already allocated");
            }

            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(0, RunningPs("qdrant"), string.Empty);
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            var exit = await ServicesUpCommand.RunCoreAsync(
                new ServicesUpCommand.Settings { Json = true },
                new DockerComposeCli(runner), runner, null, null, AnswerQdrantRefuseOllama(), CancellationToken.None);

            exit.ShouldBe(1);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        using var document = JsonDocument.Parse(stdout.ToString());
        var services = document.RootElement.GetProperty("services");
        var qdrant = services.EnumerateArray().Single(item => item.GetProperty("service").GetString() == "qdrant");
        qdrant.GetProperty("state").GetString().ShouldBe("managed");
        qdrant.GetProperty("action").GetString()!.ShouldContain("already up (managed)");
        var ollama = services.EnumerateArray().Single(item => item.GetProperty("service").GetString() == "ollama");
        ollama.GetProperty("state").GetString().ShouldBe("start-failed");
    }

    [Fact]
    public async Task PlanAsync_ManagedOverride_True_ForcesStartThroughExternal()
    {
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.FileName == "docker" && invocation.Arguments.FirstOrDefault() == "ps")
            {
                return new ProcessRunResult(0, "foreign-ollama|||0.0.0.0:11434->11434/tcp", string.Empty);
            }

            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(0, RunningPs(), string.Empty);
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });

        var overridden = await ServicesUpCommand.PlanAsync(
            new DockerComposeCli(runner), runner, AnswerOllamaRefuseQdrant(), managedOverride: true, CancellationToken.None);
        overridden.Single(d => d.Service == "ollama").Action
            .ShouldBe(ServicesUpCommand.DecisionAction.Start);

        var planned = await ServicesUpCommand.PlanAsync(
            new DockerComposeCli(runner), runner, AnswerOllamaRefuseQdrant(), managedOverride: false, CancellationToken.None);
        planned.Single(d => d.Service == "ollama").Action
            .ShouldBe(ServicesUpCommand.DecisionAction.AdoptOllama);
    }

    [Fact]
    public async Task RunCore_AbsentQdrantWithExternalOllama_PullPerService_KeepsPolicyMissing()
    {
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(0, RunningPs("qdrant"), string.Empty);
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });

        var exit = await ServicesUpCommand.RunCoreAsync(
            new ServicesUpCommand.Settings(),
            new DockerComposeCli(runner), runner, AnswerWithModelHandler(),
            null, AnswerOllamaRefuseQdrant(), CancellationToken.None);

        exit.ShouldBe(0);
        var pull = invocations.Single(i => i.Arguments.Contains("pull"));
        pull.Arguments.ShouldContain("--policy");
        pull.Arguments.ShouldContain("missing");
        pull.Arguments.ShouldContain("qdrant");
        pull.Arguments.ShouldNotContain("ollama");
    }

    [Fact]
    public async Task RunCore_ManagedOverride_ClashFailsLoudly()
    {
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.FileName == "docker" && invocation.Arguments.FirstOrDefault() == "ps")
            {
                return new ProcessRunResult(0, "foreign-ollama|||0.0.0.0:11434->11434/tcp", string.Empty);
            }

            if (invocation.Arguments.Contains("up"))
            {
                return new ProcessRunResult(1, string.Empty, "port is already allocated");
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });

        var exit = await ServicesUpCommand.RunCoreAsync(
            new ServicesUpCommand.Settings { Managed = true },
            new DockerComposeCli(runner), runner, null, null, AnswerOllamaRefuseQdrant(), CancellationToken.None);

        exit.ShouldBe(1);
        invocations.Where(i => i.Arguments.Contains("up")).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task RunCore_DryRun_Json_ReportsDecisionStatesAndTouchesNothing()
    {
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.FileName == "docker" && invocation.Arguments.FirstOrDefault() == "ps")
            {
                return new ProcessRunResult(0, "foreign-ollama|||0.0.0.0:11434->11434/tcp", string.Empty);
            }

            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(0, RunningPs("qdrant"), string.Empty);
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });
        var stdout = new StringWriter();
        var originalOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            var exit = await ServicesUpCommand.RunCoreAsync(
                new ServicesUpCommand.Settings { DryRun = true, Json = true },
                new DockerComposeCli(runner), runner, null, null, AnswerOllamaRefuseQdrant(), CancellationToken.None);

            exit.ShouldBe(0);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        invocations.ShouldNotContain(i => i.Arguments.Contains("pull"));
        invocations.ShouldNotContain(i => i.Arguments.Contains("up"));
        File.Exists(ServicesComposeFile.ComposeFilePath).ShouldBeFalse();
        using var document = JsonDocument.Parse(stdout.ToString().Trim());
        var services = document.RootElement.GetProperty("services");
        services.EnumerateArray().ShouldContain(item =>
            item.GetProperty("service").GetString() == "ollama" &&
            item.GetProperty("state").GetString() == "would-adopt");
        services.EnumerateArray().ShouldContain(item =>
            item.GetProperty("service").GetString() == "qdrant" &&
            item.GetProperty("state").GetString() == "would-start");
        services.EnumerateArray().ShouldNotContain(item =>
            item.GetProperty("state").GetString() == "start-failed");
    }

    [Fact]
    public async Task RunCore_MixedFleet_PerServicePull_KeepsPolicyMissing()
    {
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.FileName == "docker" && invocation.Arguments.FirstOrDefault() == "ps")
            {
                return new ProcessRunResult(0, "foreign-ollama|||0.0.0.0:11434->11434/tcp", string.Empty);
            }

            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(0, RunningPs("qdrant"), string.Empty);
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });
        var settings = new ServicesUpCommand.Settings();

        var exit = await ServicesUpCommand.RunCoreAsync(
            settings, new DockerComposeCli(runner), runner, null, null, AnswerOllamaRefuseQdrant(), CancellationToken.None);

        exit.ShouldBe(0);
        var pulls = invocations.Where(i => i.Arguments.Contains("pull")).ToList();
        pulls.ShouldHaveSingleItem();
        pulls[0].Arguments.ShouldContain("--policy");
        pulls[0].Arguments.ShouldContain("missing");
        pulls[0].Arguments.ShouldContain("qdrant");
        pulls[0].Arguments.ShouldNotContain("ollama");
    }

    [Fact]
    public async Task RunCore_PsUnavailableAfterStart_ReportsUnknownInsteadOfStartFailed()
    {
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(1, string.Empty, "ps failed");
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });

        var (exit, stdout, stderr) = await RunUnitAsync(
            new ServicesUpCommand.Settings { Json = true }, runner, liveness: RefuseEverything());

        exit.ShouldBe(0);
        using var document = JsonDocument.Parse(stdout.ToString().Trim());
        var services = document.RootElement.GetProperty("services");
        services.EnumerateArray().ShouldContain(item =>
            item.GetProperty("service").GetString() == "qdrant" &&
            item.GetProperty("state").GetString() == "unknown");
        stderr.ToString().ShouldNotContain("start-failed");
    }

    [Fact]
    public async Task RunCore_DockerMissingWithExternalOllama_NamesQdrantAsDockerHalf()
    {
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, _ => new ProcessRunResult(127, string.Empty, string.Empty));

        var (exit, stdout, stderr) = await RunUnitAsync(
            new ServicesUpCommand.Settings { Json = true }, runner, liveness: AnswerOllamaRefuseQdrant());

        exit.ShouldBe(1);
        stderr.ToString().ShouldContain("Docker is required for: qdrant");
        stderr.ToString().ShouldContain("ollama");
        stdout.ToString().ShouldContain("\"state\":\"blocked\"");
    }

    [Fact]
    public async Task RunCore_ComposeOwnership_GarbagePs_ReturnsUnexplained()
    {
        var runner = new ScriptedRunner(new List<ProcessInvocation>(), invocation =>
        {
            if (invocation.FileName == "docker" && invocation.Arguments.FirstOrDefault() == "ps")
            {
                return new ProcessRunResult(0, "garbage without separator", string.Empty);
            }

            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(0, "garbage ps output", string.Empty);
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });
        var source = new ComposeOwnershipSource(new DockerComposeCli(runner), runner, "/tmp/x/docker-compose.yml");

        var ownership = await source.AttributeAsync(ManagedServiceProbe.Qdrant, CancellationToken.None);

        ownership.Kind.ShouldBe(ServiceOwnership.Verdict.Unexplained);
    }

    [Theory]
    [InlineData(true, ServicesUpCommand.ModelTagPresence.Present)]
    [InlineData(false, ServicesUpCommand.ModelTagPresence.Missing)]
    internal async Task ModelPresentOnExternal_TagsBody_MatchesByNamePrefixNotRegistrySuffix(bool withModel, ServicesUpCommand.ModelTagPresence expected)
    {
        var handler = new ScriptedOllamaHandler(tagsBody: withModel
            ? $$"""{"models":[{"name":"{{AdoptModelName}}:latest"}]}"""
            : """{"models":[]}""");

        ServicesUpCommand.ModelPresentOnExternal(handler).ShouldBe(expected);
    }

    [Fact]
    public async Task ModelPresentOnExternal_TagsUnreachable_ReportsUnverifiable()
    {
        var handler = new ScriptedOllamaHandler(tagsBody: null);

        ServicesUpCommand.ModelPresentOnExternal(handler).ShouldBe(ServicesUpCommand.ModelTagPresence.Unverifiable);
    }

    private sealed class ScriptedOllamaHandler : HttpMessageHandler
    {
        private readonly string? tagsBody;

        public ScriptedOllamaHandler(string? tagsBody) => this.tagsBody = tagsBody;

        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (tagsBody is null)
            {
                throw new HttpRequestException(
                    "Connection refused", new SocketException((int)SocketError.ConnectionRefused));
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(tagsBody) };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    [Fact]
    public async Task RunCore_Adopt_EnsuresModelIntoExternalInstance_ViaInjectedHandler()
    {
        var invocations = new List<ProcessInvocation>();
        var runner = new ScriptedRunner(invocations, invocation =>
        {
            if (invocation.FileName == "docker" && invocation.Arguments.FirstOrDefault() == "ps")
            {
                return new ProcessRunResult(0, "foreign-ollama|||0.0.0.0:11434->11434/tcp", string.Empty);
            }

            if (invocation.Arguments.Contains("ps"))
            {
                return new ProcessRunResult(0, RunningPs(), string.Empty);
            }

            return invocation.Arguments.Contains("version")
                ? new ProcessRunResult(0, VersionOutput, string.Empty)
                : new ProcessRunResult(0, string.Empty, string.Empty);
        });
        var stderr = new StringWriter();
        var stdout = new StringWriter();
        var originalErr = Console.Error;
        var originalOut = Console.Out;
        Console.SetError(stderr);
        Console.SetOut(stdout);
        try
        {
            var exit = await ServicesUpCommand.RunCoreAsync(
                new ServicesUpCommand.Settings { Json = true },
                new DockerComposeCli(runner), runner, AnswerWithModelHandler(),
                _ => Task.FromResult<string?>("0.34.1"),
                AnswerOllamaRefuseQdrant(),
                CancellationToken.None);

            exit.ShouldBe(0);
            stderr.ToString().ShouldContain("Ensuring embedding model 'nomic-embed-text'");
            stdout.ToString().ShouldContain("0.34.1");
            stdout.ToString().ShouldContain("external version 0.34.1");
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SQUEEZR_HOME", _previousEnv);
        SqueezrHome.Reset();
        foreach (var dir in _createdDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static string RunningPs(params string[] services) =>
        $$"""[{{string.Join(",", services.Select(service => $$"""{"Service":"{{service}}","Name":"{{service}}-1","State":"running","Status":"Up 2 minutes"}"""))}}]""";

    private static async Task<(int Exit, StringWriter StdOut, StringWriter StdErr)> RunUnitAsync(
        ServicesUpCommand.Settings settings,
        IProcessRunner runner,
        ServiceLivenessProbe liveness)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalErr = Console.Error;
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
        }
    }

    private static ServiceLivenessProbe RefuseEverything() =>
        new(new RefuseHandler());

    private static ServiceLivenessProbe AnswerQdrantRefuseOllama() =>
        new(new AnswerQdrantRefuseOllamaHandler());

    private static ServiceLivenessProbe AnswerAlways() =>
        new(new FakeOllamaHandler());

    private static ServiceLivenessProbe AnswerOllamaRefuseQdrant() =>
        new(new FakeLivenessHandler());

    private sealed class RefuseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(
                "Connection refused", new SocketException((int)SocketError.ConnectionRefused));
    }

    private sealed class AnswerQdrantRefuseOllamaHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.OriginalString.Contains("6333")
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
                : throw new HttpRequestException(
                    "Connection refused", new SocketException((int)SocketError.ConnectionRefused));
    }

    private sealed class FakeLivenessHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.OriginalString.Contains("11434"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"version":"0.34.1"}""")
                });
            }

            throw new HttpRequestException(
                "Connection refused", new SocketException((int)SocketError.ConnectionRefused));
        }
    }

    private static HttpMessageHandler AnswerWithModelHandler() =>
        new FakeOllamaHandler();

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

    private sealed class AnswerOllamaRefuseQdrantHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.OriginalString.Contains("11434")
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))
                : throw new HttpRequestException(
                    "Connection refused", new SocketException((int)SocketError.ConnectionRefused));
    }

    private sealed class FakeOllamaHandler : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("tags"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{"models":[{"name":"{{AdoptModelName}}"}]}""")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"version":"0.5.0"}""")
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
