using System.Text.Json.Nodes;
using Squeezr.Cli.Commands;

namespace Squeezr.Cli.Mcp.Onboarding;

// The managed hooks block install arms, doctor reports and remove strips. One value per hook event so a
// later SessionStart hook adds its own block here without touching the merge rules of ManagedHooksWriter;
// identity of a managed hook object is the command token, never a marker key.
public sealed record HooksBlock(string Event, string Matcher, string CommandName, string Command)
{
    public const string LayoutUnverifiedReason = "not armed (hook layout unverified)";
    public const string ArmedState = "armed";
    public const string ProjectArmedState = "armed (project scope)";
    public const string NotArmedState = "not armed";

    public const string PreReadCommandName = "pre-read";
    public const string PostEditCommandName = "post-edit";

    public string CommandToken => "hook " + CommandName;

    public static HooksBlock? ForClient(McpClientDescriptor client, string commandPath)
    {
        var payloadId = HookPreReadToolNames.PayloadIdFor(client.Id.ToString().ToLowerInvariant());
        if (payloadId is null || client.HooksPathResolver is null)
        {
            return null;
        }
        return new HooksBlock(
            "PreToolUse",
            BuildMatcher(client.Id, [payloadId]),
            PreReadCommandName,
            $"{Invoke(commandPath)} hook {PreReadCommandName}");
    }

    public static HooksBlock? PostEditForClient(McpClientDescriptor client, string commandPath)
    {
        var payloadIds = HookPostEditToolNames.PayloadIdsFor(client.Id.ToString().ToLowerInvariant());
        if (payloadIds.Count == 0 || client.HooksPathResolver is null)
        {
            return null;
        }
        return new HooksBlock(
            "PostToolUse",
            BuildMatcher(client.Id, payloadIds),
            PostEditCommandName,
            $"{Invoke(commandPath)} hook {PostEditCommandName}");
    }

    public static IReadOnlyList<HooksBlock> ArmsFor(McpClientDescriptor client, string commandPath)
    {
        var arms = new List<HooksBlock>(2);
        if (ForClient(client, commandPath) is { } preRead)
        {
            arms.Add(preRead);
        }
        if (PostEditForClient(client, commandPath) is { } postEdit)
        {
            arms.Add(postEdit);
        }
        return arms;
    }

    // Qwen evaluates matchers as regex unless anchored, so every qwen matcher is one anchored expression
    // over the measured payload ids; Claude matches the exact tool name and takes the bare id (or its
    // alternation) as written.
    private static string BuildMatcher(McpClientId id, IReadOnlyList<string> payloadIds)
    {
        if (id != McpClientId.Qwen)
        {
            return string.Join('|', payloadIds);
        }
        return payloadIds.Count == 1
            ? $"^{payloadIds[0]}$"
            : $"^({string.Join('|', payloadIds)})$";
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
