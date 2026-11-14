using System.Text.Json;
using Spectre.Console.Cli;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

public sealed class HookSessionStartCommand : AsyncCommand<HookSessionStartCommand.Settings>
{
    private const int MaxStdinChars = 64 * 1024;

    public sealed class Settings : CommandSettings
    {
    }

    protected override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!TryReadPayload(HookStdin.Read(MaxStdinChars), out var input))
        {
            return 0;
        }

        if (!IsSessionStartEvent(input))
        {
            return 0;
        }

        var checkout = LinkedWorktreeOf(input);
        if (checkout is null)
        {
            return 0;
        }

        try
        {
            var run = await HookSessionStartVerb.InitAsync(checkout, cancellationToken);
            if (HookSessionStartContext.Build(run) is { } advice)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(advice));
                return 0;
            }

            // Nothing for the model, but a pass that failed is worth a line a human reading the transcript can
            // find: exit 0 keeps the session, stderr keeps the reason.
            if (run.ExitCode != 0)
            {
                Console.Error.WriteLine(
                    $"squeezr session-start: 'worktree init' exited {run.ExitCode} — {FirstLine(run.StdErr)}");
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            Console.Error.WriteLine($"squeezr session-start: 'worktree init' could not run — {ex.Message}");
            return 0;
        }
    }

    // An empty stdin is the hand-run shape and answers from the launch directory; a stdin that is not empty but
    // does not parse is a payload this hook was never given, and the fail-open answer to that is silence —
    // the same rule 'hook pre-read' lives under.
    private static bool TryReadPayload(string payload, out HookSessionStartInput? input)
    {
        input = null;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return true;
        }

        try
        {
            input = JsonSerializer.Deserialize<HookSessionStartInput>(payload);
            return input is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // A payload naming a different event is a hook wired to the wrong place; the field being absent is not a
    // mismatch, because not every host sends it to every event.
    private static bool IsSessionStartEvent(HookSessionStartInput? input) =>
        input?.HookEventName is not { Length: > 0 } name ||
        string.Equals(name, HookSessionStartContext.EventName, StringComparison.OrdinalIgnoreCase);

    // The one decision this hook makes on its own: whether the session stands in a LINKED checkout. Everything
    // past that line belongs to the verb, including what the store does about it. A plain directory, a repo with
    // no git marker, and the main worktree all answer with silence — the first two are nobody's index to create,
    // and the third is what 'squeezr init' and 'squeezr index' already own.
    private static string? LinkedWorktreeOf(HookSessionStartInput? input)
    {
        var start = WorkspaceOf(input);
        try
        {
            return WorktreeFacts.Resolve(start) is { IsLinked: true } facts ? facts.Toplevel : null;
        }
        catch (WorkspaceException ex)
        {
            Console.Error.WriteLine($"squeezr session-start: no worktree facts for '{start}' — {ex.Message}");
            return null;
        }
    }

    private static string WorkspaceOf(HookSessionStartInput? input)
    {
        var cwd = input?.Cwd;
        if (!string.IsNullOrWhiteSpace(cwd) && Path.IsPathRooted(cwd) && Directory.Exists(cwd))
        {
            return cwd;
        }

        // Hosts that send no cwd still start the session in the directory the agent works from, so the launch
        // directory is the same checkout — and a hook run by hand in a terminal answers like the host did.
        return Directory.GetCurrentDirectory();
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            is { Length: > 0 } lines ? lines[0] : "no output";
}
