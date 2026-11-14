using System.Text.Json.Nodes;
using Squeezr.Cli.Commands;

namespace Squeezr.Cli.Mcp.Onboarding;

// The managed hooks block install arms, doctor reports and remove strips. One value per hook event, so the
// SessionStart block lives beside the PreToolUse one without touching the merge rules of ManagedHooksWriter;
// identity of a managed hook object is the command token, never a marker key.
public sealed record HooksBlock(string Event, string Matcher, string CommandName, string Command)
{
    public const string LayoutUnverifiedReason = "not armed (hook layout unverified)";
    public const string ArmedState = "armed";
    public const string ProjectArmedState = "armed (project scope)";
    public const string NotArmedState = "not armed";

    // A first lane session may have a whole store to copy and reconcile, so the snippet buys the verb time
    // instead of letting the host kill the hook mid-pass. Both runtimes read this value as seconds, and
    // 600 stays under the point where Qwen starts treating a timeout as legacy milliseconds.
    public const int SessionStartTimeoutSeconds = 600;

    private const string PreReadCommandName = "pre-read";
    private const string SessionStartCommandName = "session-start";

    public string CommandToken => "hook " + CommandName;

    public int? TimeoutSeconds { get; init; }

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

    // The SessionStart matcher is compared against how the session began, not against a tool name: 'startup' is
    // the source string both runtimes send for a new session, and both spell the other sources the same way.
    // Narrowing to startup is the measured choice — an unanchored pattern is a regex on Qwen, and a matcher
    // nobody checked would arm a hook that never fires.
    public static HooksBlock? ForSessionStart(McpClientDescriptor client, string commandPath)
    {
        if (client.HooksPathResolver is null)
        {
            return null;
        }

        var commandToken = "hook " + SessionStartCommandName;
        return new HooksBlock("SessionStart", "startup", SessionStartCommandName, $"{Invoke(commandPath)} {commandToken}")
        {
            TimeoutSeconds = SessionStartTimeoutSeconds,
        };
    }

    public bool Owns(string candidateCommand) =>
        string.Equals(candidateCommand, Command, StringComparison.Ordinal) ||
        (candidateCommand.EndsWith(" " + CommandToken, StringComparison.Ordinal) &&
            IsSqueezrExecutable(candidateCommand[..^(" " + CommandToken).Length]));

    private static bool IsSqueezrExecutable(string executableToken) =>
        Path.GetFileName(executableToken.Trim('"')) is { } name &&
        (name.Equals("squeezr", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("squeezr.exe", StringComparison.OrdinalIgnoreCase));

    public JsonObject ToJson()
    {
        var hook = new JsonObject
        {
            ["type"] = "command",
            ["command"] = Command,
        };
        // Only the blocks that need a budget longer than the host default say so: the pre-read hook object
        // stays byte-identical to what install has always written.
        if (TimeoutSeconds is { } seconds)
        {
            hook["timeout"] = seconds;
        }

        return new JsonObject
        {
            ["matcher"] = Matcher,
            ["hooks"] = new JsonArray(hook),
        };
    }

    private static string Invoke(string commandPath)
    {
        var full = Path.GetFullPath(commandPath);
        return full.Contains(' ', StringComparison.Ordinal) ? $"\"{full}\"" : full;
    }
}
