using System.Text;
using System.Text.Json;
using Squeezr.Cli.Infra;
using Squeezr.Cli.Output;
using Squeezr.Core.Serialization;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Commands;

// What a session is told about its index, read out of the verb's own --json report instead of a second look at
// the store: the report is the surface the verb's tasks pin, so a change in what the verb answers lands here
// rather than quietly diverging from it.
//
// SessionStart text reaches the model with angle brackets escaped (measured on Qwen Code 0.27.1, where the
// context is wrapped in a hidden session-start block; Claude Code wraps the same field in a system reminder).
// Nothing this builder emits carries a bracket of its own, and every report field it interpolates goes through
// TagSafe, because a remedy spelled 'cd <path> && squeezr serve' would otherwise arrive as '&lt;path&gt;'.
public static class HookSessionStartContext
{
    public const string EventName = "SessionStart";

    private const int MaxWarningLines = 3;
    private const int MaxNoticeChars = 200;
    private const int MaxChars = 1200;
    private const string WarningPrefix = "warning: ";
    private const string ToolSentence =
        " code_search, symbol_lookup, find_references, repo_map and expand_span answer for this branch; " +
        "a plain full-file read is the slow path here.";

    public static HookSessionStartAdvice? Build(ProcessRunResult run)
    {
        var report = ReadReport(run.StdOut);
        if (report is null || !AnswersForThisCheckout(report))
        {
            return null;
        }

        var text = new StringBuilder();
        text.Append(OpeningLine(report)).Append(ToolSentence);
        text.Append(ReconcileNotice(report));
        foreach (var warning in WarningLines(run.StdErr, text))
        {
            text.Append("\n- ").Append(Notice(warning));
        }

        var context = text.Length > MaxChars
            ? text.ToString(0, MaxChars - 3) + "..."
            : text.ToString();
        return new HookSessionStartAdvice(new HookSessionStartAdviceOutput(EventName, context));
    }

    private static WorktreeInitReport? ReadReport(string stdOut)
    {
        var start = stdOut.IndexOf('{', StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorktreeInitReport>(stdOut[start..], SqueezrJson.OutputOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Only the states where a store really answers for this checkout get a word into the prompt. 'ParentNotIndexed'
    // and 'NoParentRootToName' created nothing and are the human's to fix; mentioning squeezr inside a repo that
    // was never indexed is noise nobody opted into, and it would repeat every session.
    private static bool AnswersForThisCheckout(WorktreeInitReport report) =>
        report.State is nameof(WorktreeRegistrationState.Registered)
            or nameof(WorktreeRegistrationState.AlreadyRegistered);

    private static string OpeningLine(WorktreeInitReport report) =>
        report.State == nameof(WorktreeRegistrationState.Registered)
            ? $"squeezr: this checkout is git worktree {Quoted(report.Branch)} and got its own index just now " +
              $"(store {Quoted(report.StoreKey)}, collection {Quoted(report.Collection)})."
            : $"squeezr: this checkout is git worktree {Quoted(report.Branch)} and already has its own index " +
              $"(store {Quoted(report.StoreKey)}); nothing was created this session.";

    // 'Deferred' is the ordinary answer of a lane born before its main worktree was indexed, and 'Pending' the
    // one a session has to know before trusting a line number — both are states the verb reports instead of
    // throwing, so the reason travels with them rather than being re-derived here.
    private static string ReconcileNotice(WorktreeInitReport report) =>
        report.Reconcile?.State switch
        {
            nameof(WorktreeReconcileState.Pending) =>
                $" The copied index is not reconciled to this branch yet{ReasonSuffix(report.Reconcile.Reason)} " +
                "— line numbers can still name the main worktree, and 'squeezr status' breaks the numbers down.",
            nameof(WorktreeReconcileState.Deferred) =>
                $" No reconcile pass ran here{ReasonSuffix(report.Reconcile.Reason)}.",
            _ => string.Empty,
        };

    private static string ReasonSuffix(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? string.Empty : $" ({Notice(reason)})";

    // The verb puts its warnings, its pending state and its cost notices on stderr; a session-start reader that
    // only sees stdout would miss exactly the cases where the store needs the human. A line already told by the
    // reconcile notice is not told twice — the same sentence twice in a first-turn instruction is noise.
    private static IEnumerable<string> WarningLines(string stdErr, StringBuilder soFar)
    {
        var written = soFar.ToString();
        var counted = 0;
        foreach (var raw in stdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!raw.StartsWith(WarningPrefix, StringComparison.OrdinalIgnoreCase) ||
                raw.Length <= WarningPrefix.Length)
            {
                continue;
            }

            var warning = raw[WarningPrefix.Length..].Trim();
            if (warning.Length == 0)
            {
                continue;
            }

            if (!written.Contains(warning, StringComparison.Ordinal))
            {
                yield return warning;
                if (++counted == MaxWarningLines)
                {
                    yield break;
                }
            }
        }
    }

    private static string Quoted(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : $"'{Notice(value)}'";

    private static string Notice(string value) => Trim(TagSafe(value.Trim()), MaxNoticeChars);

    private static string TagSafe(string value) => value.Replace('<', '[').Replace('>', ']');

    private static string Trim(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..(maxChars - 3)] + "...";
}
