namespace Squeezr.Cli.Commands;

// Both hook surfaces answer through this one list: a runtime that names its read tool differently is a
// missing row here, not an invitation to normalize. Exact Ordinal compare, so "read" and "READ_FILE"
// stay the unmeasured ids they are.
public static class HookPreReadToolNames
{
    public sealed record ReadToolId(string ToolId, string Runtime, bool DeliveredAsPayload);

    private static readonly ReadToolId[] ReadIds =
    [
        new("Read", "claude", DeliveredAsPayload: true),
        new("read_file", "qwen", DeliveredAsPayload: true),
        new("ReadFile", "qwen", DeliveredAsPayload: false),
    ];

    public static IReadOnlyList<ReadToolId> Identities => ReadIds;

    public static bool IsReadTool(string? toolName) =>
        toolName is not null && ReadIds.Any(id => string.Equals(id.ToolId, toolName, StringComparison.Ordinal));

    public static string? PayloadIdFor(string runtime) =>
        ReadIds.FirstOrDefault(id => id.DeliveredAsPayload &&
            string.Equals(id.Runtime, runtime, StringComparison.OrdinalIgnoreCase))?.ToolId;
}
