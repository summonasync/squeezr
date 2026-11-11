namespace Squeezr.Core.Infra.Autostart;

public interface IWatchdAutostartApplier
{
    string ManagedTarget { get; }

    Task InstallAsync(string unitText, CancellationToken cancellationToken);

    Task<AutostartRemoval> UninstallAsync(CancellationToken cancellationToken);

    Task<WatchdUnitProbe> ProbeAsync(CancellationToken cancellationToken);
}
