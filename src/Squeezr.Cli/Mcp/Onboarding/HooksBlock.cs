using System.Text.Json.Nodes;
using Squeezr.Cli.Commands;

namespace Squeezr.Cli.Mcp.Onboarding;

// The managed hooks block install arms, doctor reports and remove strips. One value per hook event so a
// later SessionStart or post-edit hook adds its own block here without touching the merge rules of
// ManagedHooksWriter; identity of a managed hook object is the command token, never a marker key.
public sealed record HooksBlock(string Event, string Matcher, string CommandName, string Command)
{
    public const string LayoutUnverifiedReason = "not armed (hook layout unverified)";
    public const string ArmedState = "armed";
    public const string ProjectArmedState = "armed (project scope)";
    public const string NotArmedState = "not armed";

    private const string PreReadCommandName = "pre-read";

    public string CommandToken => "hook " + CommandName;

    public static HooksBlock? ForClient(McpClientDescriptor client, string commandPath)
    {
        var payloadId = HookPreReadToolNames.PayloadIdFor(client.Id.ToString().ToLowerInvariant());
        if (payloadId is null || client.HooksPathResolver is null)
        {
            return null;
        }
        // Qwen evaluates matchers as regex unless anchored; Claude matches the exact tool name.
        var matcher = client.Id == McpClientId.Qwen ? $"^{payloadId}$" : payloadId;
        var commandToken = "hook " + PreReadCommandName;
        return new HooksBlock("PreToolUse", matcher, PreReadCommandName, $"{Invoke(commandPath)} {commandToken}");
    }

    public bool Owns(string candidateCommand) =>
        string.Equals(candidateCommand, Command, StringComparison.Ordinal) ||
        (candidateCommand.EndsWith(" " + CommandToken, StringComparison.Ordinal) &&
            IsSqueezrExecutable(candidateCommand[..^(" " + CommandToken).Length]));

    private static bool IsSqueezrExecutable(string executableToken) =>
        Path.GetFileName(executableToken.Trim('"')) is { } name &&
        (name.Equals("squeezr", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("squeezr.exe", StringComparison.OrdinalIgnoreCase));

    public JsonObject ToJson() => new()
    {
        ["matcher"] = Matcher,
        ["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "command",
            ["command"] = Command,
        }),
    };

    private static string Invoke(string commandPath)
    {
        var full = Path.GetFullPath(commandPath);
        return full.Contains(' ', StringComparison.Ordinal) ? $"\"{full}\"" : full;
    }
}
