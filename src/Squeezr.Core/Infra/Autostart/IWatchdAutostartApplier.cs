namespace Squeezr.Core.Infra.Autostart;

public interface IWatchdAutostartApplier
{
    string ManagedTarget { get; }

    Task InstallAsync(string unitText, CancellationToken cancellationToken);

    Task<bool> UninstallAsync(CancellationToken cancellationToken);

    Task<bool> IsInstalledAsync(CancellationToken cancellationToken);
}
