using Squeezr.Core.Infra;

namespace Squeezr.Cli.Infra;

// Docker attribution over the compose/docker CLIs (spec 069 §3.2). Our compose project is checked
// first (docker compose ps), then any container publishing the probed port (docker ps). Every
// failure shape - docker missing, ps failing, unparsable output - degrades to Unexplained, never
// to a verdict that would let a caller treat a live service as absent (plan R1).
public sealed class ComposeOwnershipSource(DockerComposeCli cli, IProcessRunner runner, string composeFile)
    : IServiceOwnershipSource
{
    public async Task<ServiceOwnership> AttributeAsync(ManagedServiceProbe probe, CancellationToken cancellationToken)
    {
        try
        {
            if (await OurComposeRunsAsync(probe, cancellationToken))
            {
                return ServiceOwnership.Ours;
            }

            var container = await ForeignContainerPublishingAsync(probe, cancellationToken);
            return container is null ? ServiceOwnership.Unexplained : ServiceOwnership.Foreign(container);
        }
        catch (Exception ex) when (ex is ServicesException or TimeoutException)
        {
            return ServiceOwnership.Unexplained;
        }
    }

    private async Task<bool> OurComposeRunsAsync(ManagedServiceProbe probe, CancellationToken cancellationToken)
    {
        var installation = await cli.DetectAsync(cancellationToken);
        var ps = await cli.RunAsync(
            installation,
            composeFile,
            ["ps", "--format", "json"],
            DockerComposeCli.StatusTimeout,
            cancellationToken);
        if (ps.ExitCode != 0)
        {
            return false;
        }

        return DockerComposeCli.ParseServiceStates(ps.StdOut, installation.Version).Any(state =>
            string.Equals(state.Service, probe.ServiceName, StringComparison.Ordinal) &&
            string.Equals(state.State, "running", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> ForeignContainerPublishingAsync(ManagedServiceProbe probe, CancellationToken ct)
    {
        var port = probe.Endpoint.Port.ToString();
        var ps = await runner.RunAsync(
            new ProcessInvocation(
                "docker",
                ["ps", "--format", "{{.Names}}|||{{.Ports}}"],
                DockerComposeCli.DetectTimeout,
                null),
            ct);
        if (ps.ExitCode != 0)
        {
            return null;
        }

        foreach (var line in ps.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split("|||");
            if (parts.Length == 2 && parts[1].Contains($":{port}->", StringComparison.Ordinal))
            {
                return parts[0];
            }
        }

        return null;
    }
}
