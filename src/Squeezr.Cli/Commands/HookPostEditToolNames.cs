namespace Squeezr.Cli.Commands;

// The edit-family ids the two armable runtimes deliver as tool_name in a PostToolUse payload, measured
// from the runtimes' own tool surfaces. Exact Ordinal compare — an id that is close but not declared
// ("NotebookEdit" on qwen, "edit" lowercase on claude) stays the unmeasured id it is, and a matcher for
// a new runtime is a row added here, not a normalization rule.
public static class HookPostEditToolNames
{
    public sealed record EditToolId(string ToolId, string Runtime);

    private static readonly EditToolId[] EditIds =
    [
        new("edit", "qwen"),
        new("write_file", "qwen"),
        new("notebook_edit", "qwen"),
        new("Edit", "claude"),
        new("Write", "claude"),
        new("MultiEdit", "claude"),
        new("NotebookEdit", "claude"),
    ];

    public static IReadOnlyList<EditToolId> Identities => EditIds;

    public static bool IsEditTool(string? toolName) =>
        toolName is not null && EditIds.Any(id => string.Equals(id.ToolId, toolName, StringComparison.Ordinal));

    public static IReadOnlyList<string> PayloadIdsFor(string runtime) =>
        EditIds
            .Where(id => string.Equals(id.Runtime, runtime, StringComparison.OrdinalIgnoreCase))
            .Select(id => id.ToolId)
            .ToList();
}
