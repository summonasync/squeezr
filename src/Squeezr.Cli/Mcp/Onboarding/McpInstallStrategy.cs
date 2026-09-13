namespace Squeezr.Cli.Mcp.Onboarding;

public static class McpInstallStrategy
{
    public static McpInstallDecision Resolve(McpClientDescriptor client, McpScope scope, ClientCliLocator locator)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(locator);
        if (client.WriteStrategy == McpWriteStrategy.WriterOnly)
        {
            return new McpInstallDecision(McpInstallMethod.Writer, null);
        }
        if (!client.SupportsCliScope && scope == McpScope.Project)
        {
            return new McpInstallDecision(
                McpInstallMethod.Writer,
                $"{client.DisplayName} does not support project scope via CLI; the config was written directly.");
        }
        if (locator.Locate(client.BinaryName) is not null)
        {
            return new McpInstallDecision(McpInstallMethod.Cli, null);
        }
        return new McpInstallDecision(
            McpInstallMethod.Writer,
            $"Client CLI '{client.BinaryName}' was not found in PATH; the config was written directly.");
    }
}
