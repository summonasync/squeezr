using System.Text.Json;
using System.Text.Json.Nodes;

namespace Squeezr.Cli.Mcp.Onboarding;

public static class JsonMcpConfigWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static async Task InstallAsync(string configPath, string serverName, JsonObject entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var root = await ReadRootAsync(configPath, cancellationToken);
        var servers = GetServersObject(root, configPath);
        servers[serverName] = (JsonObject)entry.DeepClone();
        await WriteAsync(configPath, root, cancellationToken);
    }

    public static async Task RemoveAsync(string configPath, string serverName, CancellationToken cancellationToken)
    {
        var root = await ReadRootAsync(configPath, cancellationToken);
        if (root.TryGetPropertyValue("mcpServers", out var serversNode) &&
            serversNode is JsonObject servers &&
            servers.Remove(serverName))
        {
            await WriteAsync(configPath, root, cancellationToken);
        }
    }

    private static async Task<JsonObject> ReadRootAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return [];
        }
        var json = await File.ReadAllTextAsync(configPath, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            var node = JsonNode.Parse(
                json,
                new JsonNodeOptions(),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (node is not JsonObject root)
            {
                throw new InvalidDataException($"Config root must be a JSON object: {configPath}");
            }
            _ = root.Count;
            return root;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"Config contains duplicate keys: {configPath}", ex);
        }
    }

    private static JsonObject GetServersObject(JsonObject root, string configPath)
    {
        if (!root.TryGetPropertyValue("mcpServers", out var node) || node is null)
        {
            var created = new JsonObject();
            root["mcpServers"] = created;
            return created;
        }
        if (node is not JsonObject servers)
        {
            throw new InvalidDataException($"'mcpServers' must be a JSON object in {configPath}");
        }
        return servers;
    }

    private static async Task WriteAsync(string configPath, JsonObject root, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        McpConfigBackup.Create(configPath);
        await File.WriteAllTextAsync(configPath, root.ToJsonString(SerializerOptions), cancellationToken);
    }
}
