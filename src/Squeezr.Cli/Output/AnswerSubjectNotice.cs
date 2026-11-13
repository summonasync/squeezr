using System.Text.Json.Serialization;
using Squeezr.Cli.Mcp;
using Squeezr.Core.Workspace;

namespace Squeezr.Cli.Output;

// The one wording of "which tree this answer describes", shared by the CLI verbs and the MCP envelope, because
// a query verb and a tool call answer the same question and must not become two stories about it. The field
// names are the read verb's own — rootPath, branch, storeKey — so the subject block is a sibling of a resolve
// row, never a second vocabulary for one checkout.
//
// The door is only ever reached with something to say: a main worktree answering a caller that stands inside it
// produces no subject at all, so an old consumer reads exactly the fields it read before (035).
internal static class AnswerSubjectNotice
{
    internal static AnswerSubjectBody? BodyOf(AnswerSubject? subject) =>
        subject is null
            ? null
            : new(
                SessionLocation.Display(subject.RootPath),
                subject.Branch,
                subject.StoreKey);

    // A cross-tree claim names BOTH trees or it is not a claim: the answering root and the caller's own checkout.
    // The facts door only ever sets CrossTree with a caller root it measured, and this pattern is where that
    // invariant is read rather than assumed.
    internal static string? CrossTreeWarningOf(AnswerSubject? subject) =>
        subject is { CrossTree: true, CallerRootPath: { } caller } cross
            ? $"cross-tree: the paths and line numbers in this answer name "
              + $"'{SessionLocation.Display(cross.RootPath)}', not '{SessionLocation.Display(caller)}', "
              + "the checkout this query was asked in"
            : null;

    // A naming, not a complaint: the line is printed even when the caller asked for this store on purpose, and
    // the warning is what says the ask crossed a tree. Both travel on the warning channel in both modes.
    internal static void Report(AnswerSubject? subject)
    {
        if (subject is null)
        {
            return;
        }

        Console.Error.WriteLine(
            $"subject: {SessionLocation.Display(subject.RootPath)}"
            + (subject.Branch is { } branch ? $" (branch {branch})" : " (branch unknown)")
            + (subject.StoreKey is { } key ? $" store {key}" : " no store key"));

        if (CrossTreeWarningOf(subject) is { } warning)
        {
            Console.Error.WriteLine($"warning: {warning}");
        }
    }
}

// The wire shape of the block. Three fields, because that is all the facts can prove: a detached lane has no
// branch to name and an unregistered root has no key, and an absent field reports that honestly where a guessed
// one would not.
public sealed record AnswerSubjectBody(
    [property: JsonPropertyName("rootPath")] string RootPath,
    [property: JsonPropertyName("branch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Branch,
    [property: JsonPropertyName("storeKey"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? StoreKey);
