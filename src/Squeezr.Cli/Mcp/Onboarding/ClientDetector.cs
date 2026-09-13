namespace Squeezr.Cli.Mcp.Onboarding;

public static class ClientDetector
{
    public static bool IsInstalled(McpClientDescriptor client, McpOnboardingEnvironment environment, ClientCliLocator locator)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(locator);
        if (locator.Locate(client.BinaryName) is not null)
        {
            return true;
        }
        var configPath = McpClientPaths.ConfigPath(client, environment, McpScope.User);
        if (File.Exists(configPath))
        {
            return true;
        }
        var directory = Path.GetDirectoryName(configPath);
        return directory is not null &&
            !string.Equals(directory, environment.HomeDir, StringComparison.Ordinal) &&
            Directory.Exists(directory);
    }
}
