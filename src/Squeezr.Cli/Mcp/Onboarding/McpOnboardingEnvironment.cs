namespace Squeezr.Cli.Mcp.Onboarding;

public sealed record McpOnboardingEnvironment(
    string HomeDir,
    string WorkspaceRoot,
    string CommandPath,
    string? ClientHomeOverride,
    string? SessionDirectory = null)
{
    public static McpOnboardingEnvironment Create(string workspaceRoot, string? sessionDirectory = null)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        return new(
            HomeDir: homeDir,
            WorkspaceRoot: Path.GetFullPath(workspaceRoot),
            CommandPath: StableCommandPath.Resolve(homeDir, Environment.ProcessPath),
            ClientHomeOverride: ResolveClientHomeOverride(),
            SessionDirectory: sessionDirectory);
    }

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
