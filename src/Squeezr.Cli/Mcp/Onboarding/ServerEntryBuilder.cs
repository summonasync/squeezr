using System.Text.Json.Nodes;

namespace Squeezr.Cli.Mcp.Onboarding;

public static class ServerEntryBuilder
{
    public static JsonObject Build(McpClientDescriptor client, bool watch)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        return Build(client, StableCommandPath.Resolve(homeDir, Environment.ProcessPath), watch);
    }

    public static JsonObject Build(McpClientDescriptor client, string commandPath, bool watch)
    {
        var entry = new JsonObject
        {
            ["command"] = Path.GetFullPath(commandPath),
            ["args"] = watch
                ? new JsonArray("serve", "--watch")
                : new JsonArray("serve"),
        };
        if (client.RequiredVersionNegotiation is not null)
        {
            entry["versionNegotiation"] = client.RequiredVersionNegotiation;
        }
        if (client.StartupTimeoutSeconds is { } timeout)
        {
            entry["startup_timeout_sec"] = timeout;
        }
        return entry;
    }
}
