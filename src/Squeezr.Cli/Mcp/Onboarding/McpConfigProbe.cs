using System.Text.Json;
using System.Text.Json.Nodes;

namespace Squeezr.Cli.Mcp.Onboarding;

public static class McpConfigProbe
{
    public static async Task<bool> HasServerAsync(string configPath, McpConfigFormat format, string serverName, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return false;
        }
        return format == McpConfigFormat.Json
            ? await HasJsonServerAsync(configPath, serverName, cancellationToken)
            : HasTomlServer(configPath, serverName);
    }

    private static async Task<bool> HasJsonServerAsync(string configPath, string serverName, CancellationToken cancellationToken)
    {
        JsonObject root;
        try
        {
            var json = await File.ReadAllTextAsync(configPath, cancellationToken);
            root = JsonNode.Parse(
                json,
                new JsonNodeOptions(),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new InvalidDataException($"Config root must be a JSON object: {configPath}");
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidDataException)
        {
            return false;
        }
        try
        {
            return root.TryGetPropertyValue("mcpServers", out var node) &&
                node is JsonObject servers &&
                servers.ContainsKey(serverName);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool HasTomlServer(string configPath, string serverName)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(configPath);
        }
        catch (IOException)
        {
            return false;
        }
        var section = $"mcp_servers.{serverName}";
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length < 3 || trimmed[0] != '[')
            {
                continue;
            }
            var close = trimmed.LastIndexOf(']');
            if (close < 1)
            {
                continue;
            }
            var rest = trimmed[(close + 1)..].TrimStart();
            if (rest.Length > 0 && rest[0] != '#')
            {
                continue;
            }
            var inner = trimmed[1..close].Trim();
            if (inner.StartsWith('['))
            {
                inner = inner[1..^1].Trim();
            }
            if (inner.Replace(" ", string.Empty, StringComparison.Ordinal)
                    .Replace("\t", string.Empty, StringComparison.Ordinal) == section)
            {
                return true;
            }
        }
        return false;
    }
}
