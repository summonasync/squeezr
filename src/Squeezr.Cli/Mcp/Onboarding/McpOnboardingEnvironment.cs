namespace Squeezr.Cli.Mcp.Onboarding;

public sealed record McpOnboardingEnvironment(
    string HomeDir,
    string WorkspaceRoot,
    string CommandPath,
    string? ClientHomeOverride,
    string? SessionDirectory = null)
{
    private const string ClientHomeEnvironmentVariable = "SQUEEZR_CLIENT_HOME";

    public static McpOnboardingEnvironment Create(string workspaceRoot, string? sessionDirectory = null)
    {
        var homeDir = ResolveHomeDir();
        return new(
            HomeDir: homeDir,
            WorkspaceRoot: Path.GetFullPath(workspaceRoot),
            CommandPath: StableCommandPath.Resolve(homeDir, Environment.ProcessPath),
            ClientHomeOverride: ResolveClientHomeOverride(),
            SessionDirectory: sessionDirectory);
    }

    // GetFolderPath(UserProfile) goes to the shell known-folder API on Windows and
    // ignores USERPROFILE, so tests cannot redirect the profile without this seam.
    private static string ResolveHomeDir() =>
        Environment.GetEnvironmentVariable(ClientHomeEnvironmentVariable) is { } clientHome
        && !string.IsNullOrWhiteSpace(clientHome)
            ? clientHome
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

    private static string? ResolveClientHomeOverride()
    {
        foreach (var client in McpClientRegistry.All)
        {
            if (client.HomeEnvironmentVariable is { } variable &&
                Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                return value;
            }
        }
        return null;
    }
}
