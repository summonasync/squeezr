namespace Squeezr.Core.Infra.Autostart;

public sealed class LaunchAgentApplier(
    string agentsDirectory,
    string launchDomain,
    IAutostartProcessRunner runner,
    string unitName = WatchdUnitGenerator.LaunchAgentUnitName) : IWatchdAutostartApplier
{
    private const string Launchctl = "launchctl";

    public string ManagedTarget => PlistPath;

    private string PlistFileName => $"{unitName}.plist";

    private string PlistPath => Path.Combine(agentsDirectory, PlistFileName);

    private string ServiceRef => $"{launchDomain}/{unitName}";

    // An install over an existing file writes the new plist first, then boots the loaded job out,
    // then bootstraps — a job that is loaded stays loaded through a rewrite of its file, so the
    // bootout is what makes bootstrap read the new text back. Its answer is dropped here: a plist
    // that was written but never bootstrapped legitimately reports the job as unknown, and the
    // bootstrap that follows is checked and is the step that has to succeed anyway.
    public async Task InstallAsync(string unitText, CancellationToken cancellationToken)
    {
        var replacing = File.Exists(PlistPath);
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(PlistPath, unitText);
        if (replacing)
        {
            await BootOutAsync(cancellationToken);
        }

        (await runner.RunAsync(
            Launchctl,
            ["bootstrap", launchDomain, PlistPath],
            cancellationToken)).EnsureSuccess(Launchctl, "bootstrap the launch agent");
    }

    public Task<bool> IsInstalledAsync(CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(PlistPath));

    public async Task<AutostartRemoval> UninstallAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PlistPath))
        {
            return AutostartRemoval.NothingManaged;
        }

        // The bootout answer is checked on this side only: a job that refuses to boot out stays
        // loaded until the next logoff, so the operator has to read that the daemon is still running
        // even though the managed plist is gone. The removal itself stands — deleting the file is what
        // ends the autostart for good.
        var warnings = new List<string>();
        try
        {
            (await BootOutAsync(cancellationToken))
                .EnsureSuccess(Launchctl, "boot out the launch agent");
        }
        catch (AutostartException ex)
        {
            warnings.Add(ex.Message);
        }

        File.Delete(PlistPath);
        return AutostartRemoval.Removed([.. warnings]);
    }

    private Task<AutostartProcessResult> BootOutAsync(CancellationToken cancellationToken) =>
        runner.RunAsync(Launchctl, ["bootout", ServiceRef], cancellationToken);
}
