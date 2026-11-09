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

    public async Task<bool> UninstallAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PlistPath))
        {
            return false;
        }

        await TakeDownAsync(cancellationToken);
        File.Delete(PlistPath);
        return true;
    }

    // A job that is loaded stays loaded through a rewrite of its plist, so an install over an
    // existing file takes it down first and lets bootstrap read the new text back in. The bootout
    // answer is not checked: a plist that was written but never bootstrapped legitimately reports
    // the job as unknown, and deleting the file is the step that actually ends the autostart.
    private Task TakeDownAsync(CancellationToken cancellationToken) =>
        runner.RunAsync(Launchctl, ["bootout", ServiceRef], cancellationToken);
}
