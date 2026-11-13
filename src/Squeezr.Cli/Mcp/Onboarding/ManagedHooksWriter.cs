using System.Text.Json;
using System.Text.Json.Nodes;

namespace Squeezr.Cli.Mcp.Onboarding;

public static class ManagedHooksWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static async Task<bool> UpsertAsync(string path, HooksBlock block, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(block);
        var root = await ReadRootAsync(path, cancellationToken);
        var before = root.ToJsonString(SerializerOptions);
        var entries = GetOrCreateEventArray(root, block.Event, path);
        var insertAt = entries.Count;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (!EntryCarriesManagedHook(entries[i], block))
            {
                continue;
            }
            insertAt = i;
            StripManagedHooks(entries[i], block);
            if (HookList(entries[i])?.Count == 0)
            {
                entries.RemoveAt(i);
            }
        }
        entries.Insert(insertAt, block.ToJson());
        var after = root.ToJsonString(SerializerOptions);
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return false;
        }
        await WriteAsync(path, after, cancellationToken);
        return true;
    }

    public static async Task<bool> RemoveAsync(string path, HooksBlock block, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(block);
        if (!File.Exists(path))
        {
            return false;
        }
        var root = await ReadRootAsync(path, cancellationToken);
        if (!ReclaimManagedHooks(root, block))
        {
            return false;
        }
        await WriteAsync(path, root.ToJsonString(SerializerOptions), cancellationToken);
        return true;
    }

    public static async Task<bool> IsArmedAsync(string path, HooksBlock block, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(block);
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            var root = await ReadRootAsync(path, cancellationToken);
            return root.TryGetPropertyValue("hooks", out var hooksNode) &&
                hooksNode is JsonObject hooks &&
                hooks.TryGetPropertyValue(block.Event, out var entriesNode) &&
                entriesNode is JsonArray entries &&
                entries.Any(entry => EntryCarriesManagedHook(entry, block));
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException
            or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<JsonObject> ReadRootAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }
        var json = await File.ReadAllTextAsync(path, cancellationToken);
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
                throw new InvalidDataException($"Config root must be a JSON object: {path}");
            }
            _ = root.Count;
            return root;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"Config contains duplicate keys: {path}", ex);
        }
    }

    private static JsonArray GetOrCreateEventArray(JsonObject root, string eventName, string path)
    {
        if (!root.TryGetPropertyValue("hooks", out var hooksNode) || hooksNode is null)
        {
            hooksNode = new JsonObject();
            root["hooks"] = hooksNode;
        }
        if (hooksNode is not JsonObject hooks)
        {
            throw new InvalidDataException($"'hooks' must be a JSON object in {path}");
        }
        if (!hooks.TryGetPropertyValue(eventName, out var eventNode) || eventNode is null)
        {
            eventNode = new JsonArray();
            hooks[eventName] = eventNode;
        }
        if (eventNode is not JsonArray eventEntries)
        {
            throw new InvalidDataException($"'hooks.{eventName}' must be a JSON array in {path}");
        }
        return eventEntries;
    }

    private static bool ReclaimManagedHooks(JsonObject root, HooksBlock block)
    {
        if (!root.TryGetPropertyValue("hooks", out var hooksNode) || hooksNode is not JsonObject hooks)
        {
            return false;
        }
        if (!hooks.TryGetPropertyValue(block.Event, out var eventNode) || eventNode is not JsonArray entries)
        {
            return false;
        }
        var stripped = false;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (!EntryCarriesManagedHook(entries[i], block))
            {
                continue;
            }
            stripped = true;
            StripManagedHooks(entries[i], block);
            if (HookList(entries[i])?.Count == 0)
            {
                entries.RemoveAt(i);
            }
        }
        if (!stripped)
        {
            return false;
        }
        if (entries.Count == 0)
        {
            hooks.Remove(block.Event);
        }
        if (hooks.Count == 0)
        {
            root.Remove("hooks");
        }
        return true;
    }

    private static void StripManagedHooks(JsonNode? entry, HooksBlock block)
    {
        if (HookList(entry) is not { } hooks)
        {
            return;
        }
        for (var i = hooks.Count - 1; i >= 0; i--)
        {
            if (IsManagedCommand(hooks[i], block))
            {
                hooks.RemoveAt(i);
            }
        }
    }

    private static bool EntryCarriesManagedHook(JsonNode? entry, HooksBlock block) =>
        HookList(entry) is { } hooks && hooks.Any(hook => IsManagedCommand(hook, block));

    private static bool IsManagedCommand(JsonNode? hook, HooksBlock block)
    {
        if (hook is not JsonObject command ||
            !command.TryGetPropertyValue("type", out var typeNode) ||
            GetText(typeNode) != "command" ||
            !command.TryGetPropertyValue("command", out var commandNode))
        {
            return false;
        }
        var text = GetText(commandNode);
        return text is not null && block.Owns(text);
    }

    private static JsonArray? HookList(JsonNode? entry) =>
        entry is JsonObject obj && obj.TryGetPropertyValue("hooks", out var node) ? node as JsonArray : null;

    private static string? GetText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static async Task WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
        McpConfigBackup.Create(path);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }
}
