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

    public static async Task<string?> TryGetServerCommandAsync(string configPath, McpConfigFormat format, string serverName, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }
        return format == McpConfigFormat.Json
            ? await TryGetJsonCommandAsync(configPath, serverName, cancellationToken)
            : TryGetTomlCommand(configPath, serverName);
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

    private static async Task<string?> TryGetJsonCommandAsync(string configPath, string serverName, CancellationToken cancellationToken)
    {
        try
        {
            var json = await File.ReadAllTextAsync(configPath, cancellationToken);
            var root = JsonNode.Parse(
                json,
                new JsonNodeOptions(),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
            if (root is null)
            {
                return null;
            }
            if (!root.TryGetPropertyValue("mcpServers", out var serversNode) || serversNode is not JsonObject servers)
            {
                return null;
            }
            if (!servers.TryGetPropertyValue(serverName, out var entryNode) || entryNode is not JsonObject entry)
            {
                return null;
            }
            return entry.TryGetPropertyValue("command", out var commandNode) ? commandNode?.GetValue<string>() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? TryGetTomlCommand(string configPath, string serverName)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(configPath);
        }
        catch (IOException)
        {
            return null;
        }
        var section = $"mcp_servers.{serverName}";
        var inSection = false;
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length >= 3 && trimmed[0] == '[')
            {
                var close = trimmed.LastIndexOf(']');
                if (close >= 1)
                {
                    var rest = trimmed[(close + 1)..].TrimStart();
                    if (rest.Length == 0 || rest[0] == '#')
                    {
                        var inner = trimmed[1..close].Trim();
                        if (inner.StartsWith('['))
                        {
                            inner = inner[1..^1].Trim();
                        }
                        inSection = inner.Replace(" ", string.Empty, StringComparison.Ordinal)
                            .Replace("\t", string.Empty, StringComparison.Ordinal) == section;
                        continue;
                    }
                }
                inSection = false;
                continue;
            }
            if (!inSection || trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }
            var equals = trimmed.IndexOf('=');
            if (equals < 1)
            {
                continue;
            }
            var key = trimmed[..equals].Trim();
            if (!string.Equals(key, "command", StringComparison.Ordinal))
            {
                continue;
            }
            var rawValue = trimmed[(equals + 1)..].Trim();
            return UnquoteTomlString(StripTomlInlineComment(rawValue));
        }
        return null;
    }

    private static string StripTomlInlineComment(string raw)
    {
        if (raw.Length == 0)
        {
            return raw;
        }
        var quote = raw[0];
        if (quote != '"' && quote != '\'')
        {
            var bareHash = raw.IndexOf('#');
            return bareHash < 0 ? raw : raw[..bareHash].TrimEnd();
        }
        for (var i = 1; i < raw.Length; i++)
        {
            if (quote == '"' && raw[i] == '\\')
            {
                i++;
            }
            else if (raw[i] == quote)
            {
                return raw[..(i + 1)];
            }
        }
        return raw;
    }

    private static string? UnquoteTomlString(string raw)
    {
        if (raw.Length < 2)
        {
            return null;
        }
        if (raw[0] == '"' && raw[^1] == '"')
        {
            return raw[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }
        if (raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1];
        }
        return null;
    }
}
