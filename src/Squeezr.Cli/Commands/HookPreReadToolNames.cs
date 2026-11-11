namespace Squeezr.Cli.Commands;

// Both hook surfaces answer through this one list: a runtime that names its read tool differently is a
// missing row here, not an invitation to normalize. Exact Ordinal compare, so "read" and "READ_FILE"
// stay the unmeasured ids they are.
public static class HookPreReadToolNames
{
    public static readonly string[] ReadIds = ["Read", "read_file", "ReadFile"];

    public static bool IsReadTool(string? toolName) =>
        toolName is not null && ReadIds.Contains(toolName, StringComparer.Ordinal);
}
