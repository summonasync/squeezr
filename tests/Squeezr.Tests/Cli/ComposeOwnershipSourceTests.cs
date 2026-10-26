using Shouldly;
using Squeezr.Cli.Infra;
using Squeezr.Core.Infra;

namespace Squeezr.Tests.Cli;

public class ComposeOwnershipSourceTests
{
    [Fact]
    public async Task Attribute_OurComposeShowsTheServiceRunning_ReturnsOurs()
    {
        var runner = new ScriptedRunner(
            ComposePsJson("ollama", "running"),
            DockerPsRows());
        var source = new ComposeOwnershipSource(new DockerComposeCli(runner), runner, "/tmp/x/infra/docker-compose.yml");

        var ownership = await source.AttributeAsync(ManagedServiceProbe.Ollama, CancellationToken.None);

        ownership.Kind.ShouldBe(ServiceOwnership.Verdict.Ours);
    }

    [Fact]
    public async Task Attribute_ComposeQuietAndDockerPsPublishesThePort_ReturnsForeignNamed()
    {
        var runner = new ScriptedRunner(
            ComposePsJson("qdrant", "exited"),
            DockerPsRows(("bobai-daily-assistant-ollama-1", "0.0.0.0:11434->11434/tcp")));
        var source = new ComposeOwnershipSource(new DockerComposeCli(runner), runner, "/tmp/x/infra/docker-compose.yml");

        var ownership = await source.AttributeAsync(ManagedServiceProbe.Ollama, CancellationToken.None);

        ownership.Kind.ShouldBe(ServiceOwnership.Verdict.Foreign);
        ownership.Owner.ShouldBe("bobai-daily-assistant-ollama-1");
    }

    [Fact]
    public async Task Attribute_ComposeQuietAndNoPublishedPort_ReturnsUnexplained()
    {
        var runner = new ScriptedRunner(
            ComposePsJson("qdrant", "exited"),
            DockerPsRows(("host-native-ollama", string.Empty)));
        var source = new ComposeOwnershipSource(new DockerComposeCli(runner), runner, "/tmp/x/infra/docker-compose.yml");

        var ownership = await source.AttributeAsync(ManagedServiceProbe.Ollama, CancellationToken.None);

        ownership.Kind.ShouldBe(ServiceOwnership.Verdict.Unexplained);
    }

    [Fact]
    public async Task Attribute_DockerPsUnparsable_DegradesToUnexplained()
    {
        var runner = new ScriptedRunner(
            ComposePsJson("qdrant", "exited"),
            "garbage without separator");
        var source = new ComposeOwnershipSource(new DockerComposeCli(runner), runner, "/tmp/x/infra/docker-compose.yml");

        var ownership = await source.AttributeAsync(ManagedServiceProbe.Qdrant, CancellationToken.None);

        ownership.Kind.ShouldBe(ServiceOwnership.Verdict.Unexplained);
    }

    [Fact]
    public async Task Attribute_DockerMissing_DegradesToUnexplained()
    {
        var runner = new ScriptedRunner(Exit127);
        var source = new ComposeOwnershipSource(new DockerComposeCli(runner), runner, "/tmp/x/infra/docker-compose.yml");

        var ownership = await source.AttributeAsync(ManagedServiceProbe.Qdrant, CancellationToken.None);

        ownership.Kind.ShouldBe(ServiceOwnership.Verdict.Unexplained);
    }

    private static string ComposePsJson(string service, string state) =>
        $$"""[{"Service":"{{service}}","Name":"{{service}}-1","State":"{{state}}","Status":"Up 2 minutes"}]""";

    private static string DockerPsRows(params (string Name, string Ports)[] rows) =>
        string.Join('\n', rows.Select(row => $"{row.Name}|||{row.Ports}"));

    private static ProcessRunResult Exit127 => new(127, string.Empty, string.Empty);

    private sealed class ScriptedRunner : IProcessRunner
    {
        private readonly string composePs;
        private readonly string dockerPs;

        public ScriptedRunner(string composePs, string dockerPs)
        {
            this.composePs = composePs;
            this.dockerPs = dockerPs;
        }

        public ScriptedRunner(ProcessRunResult everyCall)
            : this(everyCall.StdOut, everyCall.StdOut)
        {
        }

        public Task<ProcessRunResult> RunAsync(
            ProcessInvocation invocation, CancellationToken cancellationToken)
        {
            if (invocation.FileName == "docker" && invocation.Arguments.FirstOrDefault() == "ps")
            {
                return Task.FromResult(new ProcessRunResult(0, dockerPs, string.Empty));
            }

            if (invocation.Arguments.Contains("version"))
            {
                return Task.FromResult(new ProcessRunResult(0, "Docker Compose version v2.39.0", string.Empty));
            }

            if (invocation.Arguments.Contains("ps"))
            {
                return Task.FromResult(new ProcessRunResult(0, composePs, string.Empty));
            }

            return Task.FromResult(new ProcessRunResult(127, string.Empty, string.Empty));
        }
    }
}
