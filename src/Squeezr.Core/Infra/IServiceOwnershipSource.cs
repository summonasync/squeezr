namespace Squeezr.Core.Infra;

// Core-side contract for docker attribution; the CLI implements it over DockerComposeCli/ProcessRunner
// so the probe stays Cli-free. An attribution that cannot explain a live endpoint answers Unexplained -
// never a verdict that would let a caller treat the service as absent.
public interface IServiceOwnershipSource
{
    Task<ServiceOwnership> AttributeAsync(ManagedServiceProbe probe, CancellationToken cancellationToken);
}
