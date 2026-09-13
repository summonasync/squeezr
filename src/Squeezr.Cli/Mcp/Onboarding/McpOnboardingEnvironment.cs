namespace Squeezr.Cli.Mcp.Onboarding;

public sealed record McpOnboardingEnvironment(string HomeDir, string WorkspaceRoot, string CommandPath, string? ClientHomeOverride)
{
    public static McpOnboardingEnvironment Create(string workspaceRoot) =>
        new(
            HomeDir: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
            WorkspaceRoot: Path.GetFullPath(workspaceRoot),
            CommandPath: Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot resolve the path of the current executable."),
            ClientHomeOverride: ResolveClientHomeOverride());

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
