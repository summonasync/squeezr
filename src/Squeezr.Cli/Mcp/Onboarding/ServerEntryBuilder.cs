using System.Text.Json.Nodes;

namespace Squeezr.Cli.Mcp.Onboarding;

public static class ServerEntryBuilder
{
    public static JsonObject Build(McpClientDescriptor client, bool watch) =>
        Build(client, ResolveCurrentExecutable(), watch);

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

    private static string ResolveCurrentExecutable() =>
        Environment.ProcessPath ??
        throw new InvalidOperationException("Cannot resolve the path of the current executable.");
}
