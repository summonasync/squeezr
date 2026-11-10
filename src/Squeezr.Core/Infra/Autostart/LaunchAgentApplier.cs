namespace Squeezr.Core.Infra.Autostart;

public sealed class LaunchAgentApplier(
    string agentsDirectory,
    string launchDomain,
    IAutostartProcessRunner runner,
    string unitName = WatchdUnitGenerator.DefaultUnitName) : IWatchdAutostartApplier
{
    private const string Launchctl = "launchctl";

    public string ManagedTarget => PlistPath;

    private string PlistFileName => $"{unitName}.plist";

    private string PlistPath => Path.Combine(agentsDirectory, PlistFileName);

    private string ServiceRef => $"{launchDomain}/{unitName}";

    public async Task InstallAsync(string unitText, CancellationToken cancellationToken)
    {
        var replacing = File.Exists(PlistPath);
        Directory.CreateDirectory(agentsDirectory);
        File.WriteAllText(PlistPath, unitText);
        if (replacing)
        {
            await TakeDownAsync(cancellationToken);
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

        await TakeDownAsync(cancellationToken);
        File.Delete(PlistPath);
        return AutostartRemoval.Removed();
    }

    // An install over an existing file writes the new plist first, then boots the loaded job out,
    // then bootstraps — a job that is loaded stays loaded through a rewrite of its file, so the
    // bootout is what makes bootstrap read the new text back. The bootout answer is not checked: a
    // plist that was written but never bootstrapped legitimately reports the job as unknown, and
    // deleting the file is the step that actually ends the autostart.
    private Task TakeDownAsync(CancellationToken cancellationToken) =>
        runner.RunAsync(Launchctl, ["bootout", ServiceRef], cancellationToken);
}
