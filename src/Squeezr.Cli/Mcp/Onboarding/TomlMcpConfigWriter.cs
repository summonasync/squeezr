using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Squeezr.Cli.Mcp.Onboarding;

public static class TomlMcpConfigWriter
{
    public static async Task InstallAsync(string configPath, string serverName, JsonObject entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var lines = await ReadLinesAsync(configPath, cancellationToken);
        RejectDottedServerKeys(lines, serverName, configPath);
        var headerIndex = FindSection(lines, SectionName(serverName));
        var block = RenderSection(serverName, entry);
        List<string> output;
        if (headerIndex >= 0)
        {
            var endIndex = SectionEnd(lines, headerIndex);
            output = [.. lines.Take(headerIndex), .. block, .. lines.Skip(endIndex)];
            var tailStart = headerIndex + block.Count;
            if (tailStart < output.Count && output[tailStart].Length > 0 && block[^1].Length > 0)
            {
                output.Insert(tailStart, string.Empty);
            }
        }
        else
        {
            output = [.. lines];
            while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1]))
            {
                output.RemoveAt(output.Count - 1);
            }
            if (output.Count > 0)
            {
                output.Add(string.Empty);
            }
            output.AddRange(block);
        }
        await WriteLinesAsync(configPath, output, cancellationToken);
    }

    public static async Task RemoveAsync(string configPath, string serverName, CancellationToken cancellationToken)
    {
        var lines = await ReadLinesAsync(configPath, cancellationToken);
        var headerIndex = FindSection(lines, SectionName(serverName));
        if (headerIndex < 0)
        {
            return;
        }
        var startIndex = headerIndex > 0 && string.IsNullOrWhiteSpace(lines[headerIndex - 1])
            ? headerIndex - 1
            : headerIndex;
        var endIndex = SectionEnd(lines, headerIndex, SectionName(serverName));
        var output = new List<string>(lines.Count - (endIndex - startIndex));
        output.AddRange(lines.Take(startIndex));
        var tail = lines.Skip(endIndex).ToList();
        if (output.Count > 0 && output[^1].Length == 0 && tail.Count > 0 && tail[0].Length == 0)
        {
            tail.RemoveAt(0);
        }
        while (tail.Count > 0 && tail[^1].Length == 0)
        {
            tail.RemoveAt(tail.Count - 1);
        }
        if (output.Count > 0 && output[^1].Length > 0 && tail.Count > 0 && tail[0].Length > 0)
        {
            output.Add(string.Empty);
        }
        output.AddRange(tail);
        await WriteLinesAsync(configPath, output, cancellationToken);
    }

    private static string SectionName(string serverName) => $"mcp_servers.{serverName}";

    private static async Task<List<string>> ReadLinesAsync(string configPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(configPath))
        {
            return [];
        }
        var content = await File.ReadAllTextAsync(configPath, cancellationToken);
        return [.. content.Split('\n').Select(static line => line.TrimEnd('\r'))];
    }

    private static async Task WriteLinesAsync(string configPath, List<string> lines, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        McpConfigBackup.Create(configPath);
        await File.WriteAllTextAsync(configPath, string.Join('\n', lines) + "\n", cancellationToken);
    }

    private static void RejectDottedServerKeys(List<string> lines, string serverName, string configPath)
    {
        var section = string.Empty;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (TryReadTableHeader(line, out var tableName))
            {
                section = tableName;
                continue;
            }
            if (!TryReadKey(line, out var key))
            {
                continue;
            }
            var isConflict = section.Length == 0
                ? key == "mcp_servers" || key == SectionName(serverName) ||
                  key.StartsWith(SectionName(serverName) + ".", StringComparison.Ordinal)
                : section == "mcp_servers" &&
                  (key == serverName || key.StartsWith(serverName + ".", StringComparison.Ordinal));
            if (isConflict)
            {
                throw new InvalidDataException(
                    $"Key '{key}' at line {i + 1} in {configPath} cannot be rewritten; " +
                    $"convert it to a [{SectionName(serverName)}] table section or run 'codex mcp add'.");
            }
        }
    }

    private static int FindSection(List<string> lines, string sectionName)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (TryReadTableHeader(lines[i], out var tableName) && tableName == sectionName)
            {
                return i;
            }
        }
        return -1;
    }

    private static int SectionEnd(List<string> lines, int headerIndex, string subTablePrefix = "")
    {
        var end = headerIndex + 1;
        while (end < lines.Count &&
               !(TryReadTableHeader(lines[end], out var name) &&
                 (subTablePrefix.Length == 0 ||
                  !(name == subTablePrefix || name.StartsWith(subTablePrefix + ".", StringComparison.Ordinal)))))
        {
            end++;
        }
        return end;
    }

    private static bool TryReadTableHeader(string line, out string tableName)
    {
        tableName = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.Length < 3 || trimmed[0] != '[')
        {
            return false;
        }
        var close = trimmed.LastIndexOf(']');
        if (close < 0)
        {
            return false;
        }
        var rest = trimmed[(close + 1)..].TrimStart();
        if (rest.Length > 0 && rest[0] != '#')
        {
            return false;
        }
        var inner = trimmed[1..close].Trim();
        if (inner.StartsWith('['))
        {
            inner = inner[1..^1].Trim();
        }
        if (inner.Length == 0 || !inner.All(static c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ' ' or '\t'))
        {
            return false;
        }
        tableName = NormalizeKey(inner);
        return true;
    }

    private static bool TryReadKey(string line, out string key)
    {
        key = string.Empty;
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] is '#' or '[')
        {
            return false;
        }
        var equals = trimmed.IndexOf('=');
        if (equals < 0)
        {
            return false;
        }
        var candidate = trimmed[..equals].Trim();
        if (candidate.Length == 0 ||
            !candidate.All(static c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ' ' or '\t'))
        {
            return false;
        }
        key = NormalizeKey(candidate);
        return true;
    }

    private static string NormalizeKey(string key) =>
        key.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\t", string.Empty, StringComparison.Ordinal);

    private static List<string> RenderSection(string serverName, JsonObject entry)
    {
        var lines = new List<string> { $"[mcp_servers.{serverName}]" };
        foreach (var pair in entry)
        {
            if (pair.Key.Any(static c => !char.IsLetterOrDigit(c) && c is not '_' and not '-'))
            {
                throw new NotSupportedException($"TOML bare key is not supported: '{pair.Key}'");
            }
            lines.Add($"{pair.Key} = {RenderValue(pair.Value)}");
        }
        return lines;
    }

    private static string RenderValue(JsonNode? node) => node switch
    {
        null => throw new NotSupportedException("TOML value cannot be null"),
        JsonArray array => "[" + string.Join(", ", array.Select(RenderValue)) + "]",
        JsonValue value when value.TryGetValue<string>(out var text) => "\"" + EscapeBasicString(text) + "\"",
        JsonValue value when value.TryGetValue<bool>(out var flag) => flag ? "true" : "false",
        JsonValue value when value.TryGetValue<int>(out var integer) => integer.ToString(CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<long>(out var longInteger) => longInteger.ToString(CultureInfo.InvariantCulture),
        JsonValue value when value.TryGetValue<double>(out var number) => number.ToString(CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException($"TOML value type is not supported: {node.GetType().Name}"),
    };

    private static string EscapeBasicString(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\b': builder.Append("\\b"); break;
                case '\t': builder.Append("\\t"); break;
                case '\n': builder.Append("\\n"); break;
                case '\f': builder.Append("\\f"); break;
                case '\r': builder.Append("\\r"); break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
        return builder.ToString();
    }
}
