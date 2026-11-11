using Squeezr.Core.Infra.Autostart;

namespace Squeezr.Tests.Infra.Autostart;

// The seam 'watchd installed' is measured through: it answers with whatever probe a case needs and
// records nothing it is not asked about. Install and uninstall are here because the interface demands
// them; a probe test that ever reaches them is testing the wrong command.
internal sealed class FakeWatchdAutostartApplier(
    WatchdUnitProbe probe,
    string managedTarget = "/managed/squeezr-watchd") : IWatchdAutostartApplier
{
    internal List<string> InstalledTexts { get; } = [];

    internal AutostartRemoval Removal { get; set; } = AutostartRemoval.NothingManaged;

    public string ManagedTarget => managedTarget;

    public Task InstallAsync(string unitText, CancellationToken cancellationToken)
    {
        InstalledTexts.Add(unitText);
        return Task.CompletedTask;
    }

    public Task<AutostartRemoval> UninstallAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Removal);

    public Task<WatchdUnitProbe> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(probe);
}
